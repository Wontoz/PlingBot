namespace PlingBot.Services;

using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class ScorePollerService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FixtureDateCacheTtl = TimeSpan.FromMinutes(5);
    private const int FixtureLookupDaysForward = 7;
    private static readonly string ChannelEnvKey =
        $"DISCORD_CHANNEL_ID_{(Environment.GetEnvironmentVariable("CHANNEL_MODE") ?? "TEST").ToUpper()}";

    private readonly FootballApiClient _api;
    private readonly AnnouncementService _announcer;
    private readonly TipsConfig _tipsConfig;
    private readonly Logger _logger;
    private readonly DashboardService _dashboardService;
    private readonly PlayerMessageService _statusMessageService;
    private readonly CouponPercentageService _couponPercentageService;
    private readonly TeamRepository _teamRepo;
    private readonly PayoutScraperService _payoutScraper;
    private readonly Dictionary<DateTime, (DateTime FetchedUtc, List<Match> Matches)> _fixtureDateCache = new();
    private readonly HashSet<int> _loggedSkips = new();
    private bool _payoutCheckedAtRoundStart = false;

    public ScorePollerService(
        FootballApiClient api,
        AnnouncementService announcer,
        TipsConfig tipsConfig,
        Logger logger,
        DashboardService dashboardService,
        PlayerMessageService statusMessageService,
        CouponPercentageService couponPercentageService,
        TeamRepository teamRepo,
        PayoutScraperService payoutScraper)
    {
        _api = api;
        _announcer = announcer;
        _tipsConfig = tipsConfig;
        _logger = logger;
        _dashboardService = dashboardService;
        _statusMessageService = statusMessageService;
        _couponPercentageService = couponPercentageService;
        _teamRepo = teamRepo;
        _payoutScraper = payoutScraper;
    }

    public async Task StartPollingAsync(DiscordSocketClient client)
    {
        await InitializeAsync(client);

        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync())
            await RunPollTickAsync(client);
    }

    private async Task InitializeAsync(DiscordSocketClient client)
    {
        await _api.FetchAndApplyAccountStatusAsync();
        await InitializeFixtureIdsAsync();
        await BackfillMissingEventsAsync();
        await FetchAndStoreInjuriesAsync();
        await _couponPercentageService.RefreshIfDueAsync();
        await SyncInitialScoresAsync();

        // Goal/finish-triggered payout fetching only runs during live polling, so a bot that
        // starts (or restarts) after the round already finished would otherwise never attempt
        // it at all. Catch that case here once at startup — but only once the WHOLE round is
        // done, since payouts can't exist while any match is still pending and we'd just burn
        // the retry window on a guaranteed-empty attempt.
        bool roundFullyFinished = _tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue)
            .All(t => t.IsFinished);

        if (_tipsConfig.Data.MetaData.Payouts.Count == 0 && roundFullyFinished)
            _payoutScraper.ScheduleUpdate();

        // If StartTime has already passed when we start, the round-start check has been missed —
        // mark it done so we don't re-fire it on the first poll tick.
        var startTime = _tipsConfig.Data.MetaData.StartTime;
        if (startTime.HasValue && DateTime.UtcNow >= startTime.Value)
            _payoutCheckedAtRoundStart = true;

        var channel = GetChannel(client);
        if (channel != null)
        {
            string message = _statusMessageService.Generate(_tipsConfig.Data.MetaData.Player);
            await _dashboardService.RefreshOrCreateOnStartupAsync(channel, message);
        }

    }

    private async Task RunPollTickAsync(DiscordSocketClient client)
    {
        try
        {
            await _couponPercentageService.RefreshIfDueAsync();

            if (!_payoutCheckedAtRoundStart)
            {
                var startTime = _tipsConfig.Data.MetaData.StartTime;
                if (startTime.HasValue && DateTime.UtcNow >= startTime.Value)
                {
                    _payoutCheckedAtRoundStart = true;
                    if (_tipsConfig.Data.MetaData.Payouts.Count == 0)
                        _payoutScraper.ScheduleUpdate();
                }
            }

            await CheckScoresAsync(client);

            _dashboardService.RefreshExtraMessageIfNeeded(_statusMessageService);
            await _dashboardService.UpdateIfExistsAsync(client);
        }
        catch (Exception ex)
        {
            _logger.Error($"Polling error: {ex.Message}");
        }
    }

    private async Task InitializeFixtureIdsAsync()
    {
        var unresolvedTips = _tipsConfig.TipsMatches.ToList();
        var allFetchedMatches = new List<Match>();
        int mapped = 0;
        int loaded = 0;

        _logger.Log($"Mapping {_tipsConfig.TipsMatches.Count} tips...", ConsoleColor.Blue);

        for (int i = 0; i <= FixtureLookupDaysForward && unresolvedTips.Count > 0; i++)
        {
            var date = DateTime.UtcNow.Date.AddDays(i);
            var matchesForDate = await FetchMatchesByDateCachedAsync(date, forceRefresh: true);
            allFetchedMatches.AddRange(matchesForDate);


            foreach (var tip in unresolvedTips.ToList())
            {
                bool alreadyMapped = tip.FixtureId.HasValue;
                bool wasFuzzy = false;
                Match? match;

                if (alreadyMapped)
                {
                    match = matchesForDate.FirstOrDefault(m => m.Id == tip.FixtureId!.Value);
                }
                else
                {
                    match = FindMatchExact(matchesForDate, tip.HomeKey, tip.AwayKey);
                    if (match == null)
                    {
                        match = FindMatchFuzzy(matchesForDate, tip.HomeKey, tip.AwayKey);
                        wasFuzzy = match != null;
                    }

                    if (match == null)
                    {
                        var homeApi = _teamRepo.FindByName(tip.HomeTeam)?.ApiName;
                        var awayApi = _teamRepo.FindByName(tip.AwayTeam)?.ApiName;
                        if (homeApi != null && awayApi != null)
                        {
                            match = FindMatchExact(matchesForDate, homeApi, awayApi);
                            if (match == null)
                            {
                                match = FindMatchFuzzy(matchesForDate, homeApi, awayApi);
                                wasFuzzy = match != null;
                            }
                        }
                    }
                }

                if (match == null)
                    continue;

                unresolvedTips.Remove(tip);
                tip.FixtureId = match.Id;
                tip.HomeTeamId ??= match.HomeTeamId;
                tip.AwayTeamId ??= match.AwayTeamId;
                tip.KickoffUtc = match.Date.ToUniversalTime();
                tip.Match = match;
                StoreLeagueInfo(match);

                _teamRepo.Upsert(tip.HomeTeam, match.HomeTeam, match.HomeTeamId);
                _teamRepo.Upsert(tip.AwayTeam, match.AwayTeam, match.AwayTeamId);

                if (alreadyMapped)
                {
                    loaded++;
                }
                else
                {
                    string fuzzyTag = wasFuzzy ? " [fuzzy]" : "";
                    _logger.Log($"Mapped tip #{tip.Number,-2} -> fixture {match.Id} ({match.HomeTeam} vs {match.AwayTeam}) {match.Date:yyyy-MM-dd HH:mm}{fuzzyTag}", ConsoleColor.Green);
                    mapped++;
                }
            }
        }

        foreach (var tip in unresolvedTips.ToList())
        {
            if (tip.FixtureId.HasValue)
            {
                var match = await _api.FetchFixtureByIdAsync(tip.FixtureId.Value);
                if (match != null)
                {
                    unresolvedTips.Remove(tip);
                    tip.HomeTeamId ??= match.HomeTeamId;
                    tip.AwayTeamId ??= match.AwayTeamId;
                    tip.KickoffUtc = match.Date.ToUniversalTime();
                    tip.Match = match;
                    StoreLeagueInfo(match);
                    continue;
                }
            }

            _logger.Log($"Failed to map tip #{tip.Number,-2} ({tip.HomeKey} vs {tip.AwayKey})", ConsoleColor.DarkRed);
            var candidates = allFetchedMatches
                .Where(m => TeamMatchesFuzzy(m.HomeTeam, tip.HomeKey) || TeamMatchesFuzzy(m.AwayTeam, tip.AwayKey)
                         || TeamMatchesFuzzy(m.HomeTeam, tip.AwayKey) || TeamMatchesFuzzy(m.AwayTeam, tip.HomeKey))
                .Take(3);
            foreach (var c in candidates)
                _logger.Log($"  Kandidat: {c.HomeTeam} vs {c.AwayTeam} (fixture {c.Id})", ConsoleColor.Yellow);
        }

        _tipsConfig.SaveToJson();
        _logger.Log($"Mapping complete: {mapped} mapped, {loaded} loaded, {unresolvedTips.Count} failed", ConsoleColor.Cyan);
    }

    private async Task FetchAndStoreInjuriesAsync()
    {
        var tipsToFetch = _tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue && !t.IsFinished)
            .ToList();

        if (tipsToFetch.Count == 0)
            return;

        var ids = tipsToFetch.Select(t => t.FixtureId!.Value).ToList();
        var injuries = await _api.FetchInjuriesAsync(ids);

        // Migrate: remove any legacy injury events stored in the shared Events list
        _tipsConfig.Data.Events.RemoveAll(e => e.Type == "Injury");

        var tipByFixture = tipsToFetch.ToDictionary(t => t.FixtureId!.Value);

        // Clear injuries for all fetched tips so recovered players disappear
        foreach (var tip in tipsToFetch)
            tip.Injuries = [];

        var byFixture = injuries.GroupBy(i => i.FixtureId);
        foreach (var group in byFixture)
        {
            if (!tipByFixture.TryGetValue(group.Key, out var tip)) continue;

            tip.Injuries = group.DistinctBy(i => i.PlayerId).Select(injury =>
            {
                string teamName = injury.TeamId == tip.HomeTeamId ? tip.HomeTeam
                                : injury.TeamId == tip.AwayTeamId  ? tip.AwayTeam
                                : injury.TeamName;

                string statusText = string.Equals(injury.PlayerType, "Missing Fixture", StringComparison.OrdinalIgnoreCase)
                    ? "Missar matchen" : "Tveksam";
                string text = $"🩹 {statusText}: {injury.PlayerName}" +
                              (injury.Reason != null ? $" ({injury.Reason})" : "");

                return new CouponEvent
                {
                    Key       = $"injury|{injury.FixtureId}|{injury.PlayerId}",
                    Type      = "Injury",
                    FixtureId = injury.FixtureId,
                    Detail    = injury.PlayerType,
                    TeamId    = injury.TeamId,
                    Team      = teamName,
                    PlayerId  = injury.PlayerId,
                    Player    = injury.PlayerName,
                    Comments  = injury.Reason,
                    Text      = text,
                    CreatedUtc = DateTime.UtcNow
                };
            }).ToList();
        }

        _tipsConfig.SaveToJson();
        _logger.Log($"Injuries: {injuries.Count} fetched across {byFixture.Count()} fixtures", ConsoleColor.Cyan);
    }

    private async Task SyncInitialScoresAsync()
    {

        var ids = _tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue)
            .Select(t => t.FixtureId!.Value)
            .ToList();

        if (ids.Count == 0)
            return;

        var batchResults = await _api.FetchCouponFixturesBatchAsync(ids);
        var resultMap = batchResults.ToDictionary(r => r.Match.Id);

        foreach (var tip in _tipsConfig.TipsMatches.Where(t => t.FixtureId.HasValue))
        {
            if (!resultMap.TryGetValue(tip.FixtureId!.Value, out var result))
            {
                _logger.Log($"No initial data for fixture {tip.FixtureId} (tip #{tip.Number})", ConsoleColor.DarkRed);
                continue;
            }

            var current = result.Match;
            bool isInPlay =
                current.Status.Short.Equals("1H", StringComparison.OrdinalIgnoreCase) ||
                current.Status.Short.Equals("2H", StringComparison.OrdinalIgnoreCase) ||
                current.Status.Short.Equals("HT", StringComparison.OrdinalIgnoreCase) ||
                current.Status.Short.Equals("LIVE", StringComparison.OrdinalIgnoreCase);
            if (!isInPlay)
                UpdateTipScore(tip, current);
            tip.HomeTeamId ??= current.HomeTeamId;
            tip.AwayTeamId ??= current.AwayTeamId;
            tip.KickoffUtc = current.Date.ToUniversalTime();
            tip.Match = current;
            tip.Statistics ??= result.Statistics;
            StoreLeagueInfo(current);

        }

        _tipsConfig.SaveToJson();
    }

    private async Task CheckScoresAsync(DiscordSocketClient client)
    {
        var channel = GetChannel(client);
        if (channel == null || !HasMatchesInPlay())
            return;

        var ids = _tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue && !t.IsFinished)
            .Select(t => t.FixtureId!.Value)
            .ToList();

        if (ids.Count == 0)
            return;

        var batchResults = await _api.FetchCouponFixturesBatchAsync(ids);
        var resultMap = batchResults.ToDictionary(r => r.Match.Id);

        bool anyPolled = false;
        foreach (var tip in _tipsConfig.TipsMatches)
            anyPolled |= await ProcessTipAsync(channel, tip, resultMap);

        if (anyPolled)
            _logger.Log("-----------------------------------------------------------------------", ConsoleColor.DarkYellow);
    }

    private async Task<List<Match>> FetchMatchesByDateCachedAsync(DateTime date, bool forceRefresh = false)
    {
        date = date.Date;

        if (!forceRefresh &&
            _fixtureDateCache.TryGetValue(date, out var cached) &&
            DateTime.UtcNow - cached.FetchedUtc < FixtureDateCacheTtl)
        {
            return cached.Matches;
        }

        var matches = await _api.FetchMatchesByDateAsync(date);
        _fixtureDateCache[date] = (DateTime.UtcNow, matches);
        return matches;
    }

    private async Task<bool> ProcessTipAsync(IMessageChannel channel, TipsMatch tip, Dictionary<int, FixtureBatchResult> resultMap)
    {
        if (!ShouldProcessTip(tip))
            return false;

        if (!tip.FixtureId.HasValue || !resultMap.TryGetValue(tip.FixtureId.Value, out var result))
            return false;

        var current = result.Match;
        tip.Match = current;
        tip.StatusShort = current.Status.Short;
        StoreLeagueInfo(current);

        // Lineups arrive in every batch response; store them the first time they appear.
        if (tip.HomeLineup == null && result.HomeLineup != null && result.AwayLineup != null)
        {
            tip.HomeLineup = result.HomeLineup;
            tip.AwayLineup = result.AwayLineup;
            _logger.Log($"Fetched lineups for tip #{tip.Number} ({tip.HomeTeam} vs {tip.AwayTeam})", ConsoleColor.Green);
            _tipsConfig.SaveToJson();
        }

        if (ShouldSkipStatus(current.Status.Short))
        {
            _tipsConfig.SaveToJson();
            if (_loggedSkips.Add(tip.FixtureId.Value))
            {
                string kickoff = tip.KickoffUtc?.ToLocalTime().ToString("dd-MM HH:mm") ?? "";
                _logger.Log($"Match #{tip.Number,-2}  {tip.HomeTeam} - {tip.AwayTeam}  {current.Status.Long}  {kickoff}", ConsoleColor.DarkYellow);
            }
            return false;
        }

        _loggedSkips.Remove(tip.FixtureId.Value);
        LogPolledMatch(tip, current);

        tip.HomeTeamId ??= current.HomeTeamId;
        tip.AwayTeamId ??= current.AwayTeamId;
        tip.KickoffUtc = current.Date.ToUniversalTime();

        if (IsFinishedStatus(current.Status.Short))
        {
            // AET/PEN = match decided in extra time or penalties — goals scored in ET/shootout
            // must not be announced (ET is intentionally out of scope for live announcements).
            bool isExtraTimeFinish = current.Status.Short.Equals("AET", StringComparison.OrdinalIgnoreCase) ||
                                     current.Status.Short.Equals("PEN", StringComparison.OrdinalIgnoreCase);
            if (!isExtraTimeFinish)
                await _announcer.ProcessMatchUpdateAsync(channel, tip, result.Events, result.Statistics);
            HandleFinishedMatch(tip, current);
            return true;
        }

        tip.Elapsed = current.Elapsed > 0 ? current.Elapsed : tip.Elapsed;
        tip.Extra   = current.Extra > 0 ? current.Extra : 0;
        _tipsConfig.SaveToJson();

        await _announcer.ProcessMatchUpdateAsync(channel, tip, result.Events, result.Statistics);
        return true;
    }

    private async Task BackfillMissingEventsAsync()
    {
        var tipsToBackfill = _tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue &&
                        // Re-check matches that were already marked complete by an older version
                        // of this method, before Statistics/lineups were added to the backfill —
                        // otherwise they'd be skipped forever and never get the newer data.
                        (!t.BackfillComplete || t.Statistics == null || t.HomeLineup == null) &&
                        (t.IsFinished || (t.Match != null && IsFinishedStatus(t.Match.Status.Short))))
            .ToList();

        if (tipsToBackfill.Count == 0)
            return;

        var ids = tipsToBackfill.Select(t => t.FixtureId!.Value).ToList();
        var batchResults = await _api.FetchCouponFixturesBatchAsync(ids);
        var resultMap = batchResults.ToDictionary(r => r.Match.Id);

        foreach (var tip in tipsToBackfill)
        {
            if (!resultMap.TryGetValue(tip.FixtureId!.Value, out var result))
            {
                _logger.Log($"Backfill skipped for tip #{tip.Number}: fixture {tip.FixtureId} not found, will retry next startup", ConsoleColor.DarkYellow);
                continue;
            }
            BackfillTip(tip, result);
        }

        // Always persist — BackfillComplete may have flipped even when no new events were added,
        // and that flag must survive a restart or we'd keep re-fetching finished matches forever.
        _tipsConfig.Data.Events.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
        _tipsConfig.SaveToJson();
    }

    private int BackfillTip(TipsMatch tip, FixtureBatchResult result)
    {
        if (!tip.FixtureId.HasValue)
            return 0;

        var existingForFixture = _tipsConfig.Data.Events
            .Where(e => e.FixtureId == tip.FixtureId.Value)
            .ToList();

        var existingKeys = existingForFixture.Select(e => e.Key).ToHashSet();

        // Secondary fingerprint: catches duplicates where live and backfill used different key formats
        var existingFingerprints = existingForFixture
            .Select(e => $"{e.FixtureId}|{e.Type}|{e.TeamId}|{e.PlayerId}|{e.Elapsed}|{e.Extra}")
            .ToHashSet();

        var couponEvents = BuildBackfilledEvents(tip, result.Events)
            .Where(e => !existingKeys.Contains(e.Key))
            .Where(e => !existingFingerprints.Contains($"{e.FixtureId}|{e.Type}|{e.TeamId}|{e.PlayerId}|{e.Elapsed}|{e.Extra}"))
            .ToList();

        tip.Statistics ??= result.Statistics;

        if (tip.HomeLineup == null && result.HomeLineup != null && result.AwayLineup != null)
        {
            tip.HomeLineup = result.HomeLineup;
            tip.AwayLineup = result.AwayLineup;
        }

        // The batch fetch succeeded — data won't change again, so skip on every future startup
        // regardless of whether new events were found.
        tip.BackfillComplete = true;

        if (couponEvents.Count == 0)
            return 0;

        foreach (var ev in couponEvents)
            _tipsConfig.Data.Events.Add(ev);

        _logger.Log($"Backfilled {couponEvents.Count} events for tip #{tip.Number} ({tip.HomeTeam} vs {tip.AwayTeam})", ConsoleColor.Green);
        return couponEvents.Count;
    }

    internal static List<CouponEvent> BuildBackfilledEvents(TipsMatch tip, List<MatchEvent> events)
    {
        var result = new List<CouponEvent>();
        int home = 0, away = 0;

        var filtered = events
            .Where(e => e.Elapsed <= 90) // Exclude extra time — coupons only count regular 90 minutes
            .Where(e =>
                (e.Type == "Goal" && !string.Equals(e.Detail, "Missed Penalty", StringComparison.OrdinalIgnoreCase)) ||
                (e.Type == "Card" && (string.Equals(e.Detail, "Red Card", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(e.Detail, "Yellow Red Card", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(e.Detail, "Yellow Card", StringComparison.OrdinalIgnoreCase))) ||
                (e.Type == "Var" && (string.Equals(e.Detail, "Goal cancelled", StringComparison.OrdinalIgnoreCase) ||
                                     (e.Detail != null && e.Detail.StartsWith("Goal Disallowed", StringComparison.OrdinalIgnoreCase)))) ||
                string.Equals(e.Type, "subst", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Helpers.GetEventSortValue);

        foreach (var e in filtered)
        {
            string minute = Helpers.GetMinute(e);
            DateTime approxTime = tip.KickoffUtc?.AddMinutes(e.Elapsed + e.Extra) ?? DateTime.UtcNow;
            CouponEvent? couponEvent = null;

            if (e.Type == "Goal")
            {
                bool isOwnGoal  = string.Equals(e.Detail, "Own Goal", StringComparison.OrdinalIgnoreCase);
                bool isPenalty  = string.Equals(e.Detail, "Penalty",  StringComparison.OrdinalIgnoreCase);
                bool scorerIsHome = e.TeamId == tip.HomeTeamId;

                if (isOwnGoal) { if (scorerIsHome) away++; else home++; }
                else           { if (scorerIsHome) home++; else away++; }

                string currentSymbol = home > away ? "1" : home < away ? "2" : "X";
                string symbol = Helpers.GetEventSymbol(tip, currentSymbol);
                string score  = Helpers.FormatScore(home, away, isOwnGoal ? !scorerIsHome : scorerIsHome);
                string detail = isOwnGoal ? " (Självmål)" : isPenalty ? " (Straff)" : "";
                string player = !string.IsNullOrWhiteSpace(e.Player) ? $" - {e.Player}{detail}" : detail;

                couponEvent = new CouponEvent
                {
                    Key        = $"{e.FixtureId}-Goal-{e.Elapsed}-{e.Extra}-{e.PlayerId}",
                    Type       = "Goal",
                    FixtureId  = e.FixtureId,
                    Detail     = e.Detail,
                    TeamId     = e.TeamId,
                    Team       = scorerIsHome ? tip.HomeTeam : tip.AwayTeam,
                    Elapsed    = e.Elapsed,
                    Extra      = e.Extra,
                    Score      = $"{home}-{away}",
                    Text       = $"⚽ {symbol} Mål! {tip.HomeTeam} {score} {tip.AwayTeam} {minute}{player}",
                    PlayerId   = e.PlayerId,
                    Player     = e.Player,
                    AssistId   = e.AssistId,
                    Assist     = e.Assist,
                    CreatedUtc = approxTime
                };
            }
            else if (e.Type == "Card")
            {
                bool isHome   = e.TeamId == tip.HomeTeamId;
                string team   = isHome ? tip.HomeTeam : tip.AwayTeam;
                string currentSymbol = home > away ? "1" : home < away ? "2" : "X";
                string symbol = Helpers.GetEventSymbol(tip, currentSymbol, team, isHomeEvent: isHome, isBadEvent: true);
                string player = !string.IsNullOrWhiteSpace(e.Player) ? $" - {e.Player}" : "";
                bool isYellow = string.Equals(e.Detail, "Yellow Card", StringComparison.OrdinalIgnoreCase);
                string cardEmoji = isYellow ? "🟨" : "🟥";
                string cardLabel = isYellow ? "Gult kort!" : "Rött kort!";

                couponEvent = new CouponEvent
                {
                    Key        = $"{e.FixtureId}-Card-{e.Elapsed}-{e.Extra}-{e.PlayerId}",
                    Type       = "Card",
                    FixtureId  = e.FixtureId,
                    Detail     = e.Detail,
                    TeamId     = e.TeamId,
                    Team       = team,
                    Elapsed    = e.Elapsed,
                    Extra      = e.Extra,
                    Score      = "",
                    Text       = $"{cardEmoji} {symbol} {cardLabel} {team}{player} {minute}",
                    PlayerId   = e.PlayerId,
                    Player     = e.Player,
                    Comments   = e.Comments,
                    CreatedUtc = approxTime
                };
            }
            else if (string.Equals(e.Type, "subst", StringComparison.OrdinalIgnoreCase))
            {
                bool isHome = e.TeamId == tip.HomeTeamId;
                string team = isHome ? tip.HomeTeam : tip.AwayTeam;
                string playerOut = e.Player ?? "?";
                string playerIn  = e.Assist ?? "?";

                couponEvent = new CouponEvent
                {
                    Key        = $"{e.FixtureId}-Subst-{e.Elapsed}-{e.Extra}-{e.PlayerId}",
                    Type       = "Substitution",
                    FixtureId  = e.FixtureId,
                    Detail     = e.Detail,
                    TeamId     = e.TeamId,
                    Team       = team,
                    Elapsed    = e.Elapsed,
                    Extra      = e.Extra,
                    Score      = $"{home}-{away}",
                    Text       = $"🔄 Byte: {team} · UT {playerOut} IN {playerIn} {minute}",
                    PlayerId   = e.PlayerId,
                    Player     = e.Player,
                    AssistId   = e.AssistId,
                    Assist     = e.Assist,
                    CreatedUtc = approxTime
                };
            }
            else if (e.Type == "Var")
            {
                bool isHome   = e.TeamId == tip.HomeTeamId;
                string team   = isHome ? tip.HomeTeam : tip.AwayTeam;
                string currentSymbol = home > away ? "1" : home < away ? "2" : "X";
                string symbol = Helpers.GetEventSymbol(tip, currentSymbol, team, isHomeEvent: isHome, isBadEvent: true);
                string score  = Helpers.FormatScore(home, away, isHome);
                string player = !string.IsNullOrWhiteSpace(e.Player) ? $" - {e.Player}" : "";

                couponEvent = new CouponEvent
                {
                    Key        = $"{e.FixtureId}-Var-{e.Elapsed}-{e.Extra}-{e.PlayerId}",
                    Type       = "CancelledGoal",
                    FixtureId  = e.FixtureId,
                    Detail     = e.Detail,
                    TeamId     = e.TeamId,
                    Team       = team,
                    Elapsed    = e.Elapsed,
                    Extra      = e.Extra,
                    Score      = $"{home}-{away}",
                    Text       = $"⚠️ {symbol} Mål bortdömt! {tip.HomeTeam} {score} {tip.AwayTeam} {minute}{player}",
                    PlayerId   = e.PlayerId,
                    Player     = e.Player,
                    Comments   = e.Comments,
                    CreatedUtc = approxTime
                };
            }

            if (couponEvent != null)
                result.Add(couponEvent);
        }

        return result;
    }

    private IMessageChannel? GetChannel(DiscordSocketClient client)
    {
        var channelIdRaw = Environment.GetEnvironmentVariable(ChannelEnvKey);

        if (!ulong.TryParse(channelIdRaw, out var channelId))
        {
            _logger.Error($"{ChannelEnvKey} missing or invalid");
            return null;
        }

        var channel = client.GetChannel(channelId) as IMessageChannel;

        if (channel == null)
        {
            _logger.Error($"Discord channel not found: {channelId}");
            return null;
        }

        return channel;
    }

    private bool HasMatchesInPlay()
    {
        var now = DateTime.UtcNow;
        return _tipsConfig.TipsMatches.Any(tip =>
            tip.FixtureId.HasValue &&
            !tip.IsFinished &&
            tip.KickoffUtc.HasValue &&
            tip.KickoffUtc.Value <= now);
    }

    private static bool ShouldProcessTip(TipsMatch tip)
    {
        return tip.FixtureId.HasValue && !tip.IsFinished;
    }

    private void StoreLeagueInfo(Match match)
    {
        if (string.IsNullOrEmpty(match.LeagueName))
            return;
        _tipsConfig.Data.MetaData.LeagueMap[match.Id] = new Config.LeagueInfo
        {
            Name = match.LeagueName,
            Flag = match.LeagueFlag,
            Logo = match.LeagueLogo,
            Round = match.LeagueRound,
            VenueName = match.VenueName,
        };
    }

    private static bool ShouldSkipStatus(string status) =>
        status is "NS" or "TBD" or "ET" or "BT" or "P";

    private static bool IsFinishedStatus(string status)
    {
        return status.Equals("FT", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("AET", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("PEN", StringComparison.OrdinalIgnoreCase);
    }

    private static Match? FindMatchExact(IEnumerable<Match> matches, string homeKey, string awayKey)
    {
        return matches.FirstOrDefault(m =>
            TeamMatches(m.HomeTeam, homeKey) &&
            TeamMatches(m.AwayTeam, awayKey));
    }

    private static Match? FindMatchFuzzy(IEnumerable<Match> matches, string homeKey, string awayKey)
    {
        return matches.FirstOrDefault(m =>
            TeamMatchesFuzzy(m.HomeTeam, homeKey) &&
            TeamMatchesFuzzy(m.AwayTeam, awayKey));
    }

    private static bool TeamMatches(string apiTeam, string tipTeam)
    {
        return string.Equals(NormalizeTeamName(apiTeam), NormalizeTeamName(tipTeam), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TeamMatchesFuzzy(string apiTeam, string tipTeam)
    {
        var a = NormalizeTeamName(apiTeam);
        var b = NormalizeTeamName(tipTeam);
        return a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTeamName(string value)
    {
        return value.Trim().Replace(".", "").Replace("-", " ").Replace("  ", " ");
    }

    private void HandleFinishedMatch(TipsMatch tip, Match current)
    {
        UpdateTipScore(tip, current);
        tip.LastUpdatedUtc = DateTime.UtcNow;
        tip.IsFinished = true;
        tip.Outcome = current.Symbol;
        tip.StatusShort = "FT";
        tip.Elapsed = null;
        tip.Extra   = null;

        _tipsConfig.SaveToJson();
    }

    private static void UpdateTipScore(TipsMatch tip, Match match)
    {
        tip.LastHomeGoals = match.HomeGoals;
        tip.LastAwayGoals = match.AwayGoals;
        tip.HomeScore = match.HomeGoals;
        tip.AwayScore = match.AwayGoals;
    }

    private void LogPolledMatch(TipsMatch tip, Match current)
    {
        _logger.Log($"Match #{tip.Number,-2}  {tip.HomeTeam} - {tip.AwayTeam} {current.HomeGoals}-{current.AwayGoals} ({current.Status.Long}, {FormatMatchMinute(current)})", ConsoleColor.DarkYellow);
    }

    private static string FormatMatchMinute(Match match)
    {
        string minute = match.Extra > 0
            ? $"{match.Elapsed}+{match.Extra}"
            : $"{match.Elapsed}";

        return $"{minute}'";
    }
}
