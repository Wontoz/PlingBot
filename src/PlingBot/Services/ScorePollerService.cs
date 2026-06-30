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
    private readonly Dictionary<int, DateTime> _lastLineupCheck = new();
    // API-Sports updates the lineups endpoint every 15 min — checking more often than that
    // just re-fetches the same (still empty) data. Lookahead starts a bit before the
    // documented 20-40 min pre-kickoff publish window, not 75 min of guaranteed-empty checks.
    private static readonly TimeSpan LineupCheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LineupLookaheadWindow = TimeSpan.FromMinutes(45);

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

        _logger.Log($"Mapping {_tipsConfig.TipsMatches.Count} tips day-by-day, max {FixtureLookupDaysForward + 1} days", ConsoleColor.Blue);

        for (int i = 0; i <= FixtureLookupDaysForward && unresolvedTips.Count > 0; i++)
        {
            var date = DateTime.UtcNow.Date.AddDays(i);
            var matchesForDate = await FetchMatchesByDateCachedAsync(date, forceRefresh: true);
            allFetchedMatches.AddRange(matchesForDate);

            _logger.Log($"Fetched {matchesForDate.Count} fixtures for {date:yyyy-MM-dd}", ConsoleColor.DarkBlue);

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
                    _logger.Log($"Loaded tip #{tip.Number,-2} fixture {match.Id} ({match.HomeTeam} vs {match.AwayTeam}) {match.Date:yyyy-MM-dd HH:mm}", ConsoleColor.Green);
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
                    _logger.Log($"Loaded tip #{tip.Number,-2} via direct fixture lookup {match.Id} ({match.HomeTeam} vs {match.AwayTeam})", ConsoleColor.Green);
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

    private async Task SyncInitialScoresAsync()
    {
        _logger.Log("Initial sync: scores", ConsoleColor.Blue);

        var matches = await FetchMatchesForTipDatesAsync();

        foreach (var tip in _tipsConfig.TipsMatches.Where(t => t.FixtureId.HasValue))
        {
            var current = matches.FirstOrDefault(m => m.Id == tip.FixtureId!.Value);

            if (current == null)
            {
                _logger.Log($"No initial data for fixture {tip.FixtureId} (tip #{tip.Number})", ConsoleColor.DarkRed);
                continue;
            }

            UpdateTipScore(tip, current);
            tip.HomeTeamId ??= current.HomeTeamId;
            tip.AwayTeamId ??= current.AwayTeamId;
            tip.KickoffUtc = current.Date.ToUniversalTime();
            tip.Match = current;
            StoreLeagueInfo(current);

            _logger.Log($"Initial sync tip #{tip.Number}: {current.HomeGoals}-{current.AwayGoals} ({current.Status.Long})", ConsoleColor.DarkCyan);
        }

        _tipsConfig.SaveToJson();
    }

    private async Task CheckScoresAsync(DiscordSocketClient client)
    {
        var channel = GetChannel(client);
        if (channel == null)
            return;

        var matches = await FetchMatchesForTipDatesAsync();
        bool anyPolled = false;

        foreach (var tip in _tipsConfig.TipsMatches)
            anyPolled |= await ProcessTipAsync(channel, tip, matches);

        if (anyPolled)
            _logger.Log("-----------------------------------------------------------------------", ConsoleColor.DarkYellow);
    }

    private async Task<List<Match>> FetchMatchesForTipDatesAsync()
    {
        DateTime today = DateTime.UtcNow.Date;

        var todayAndPastDates = _tipsConfig.TipsMatches
            .Where(tip => tip.FixtureId.HasValue && !tip.IsFinished)
            .Select(tip => tip.Match?.Date.Date ?? today)
            .Where(date => date <= today)
            .Append(today)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        var matches = new List<Match>();

        foreach (var date in todayAndPastDates)
            matches.AddRange(await FetchMatchesByDateCachedAsync(date));

        // Future matches haven't started — reuse the data loaded at startup, no API call needed
        foreach (var tip in _tipsConfig.TipsMatches.Where(t => t.FixtureId.HasValue && !t.IsFinished))
            if (tip.Match?.Date.Date > today && matches.All(m => m.Id != tip.Match.Id))
                matches.Add(tip.Match);

        // Overlay live data only when at least one match has passed its scheduled kickoff
        if (HasMatchesInPlay())
        {
            var liveMatches = await _api.FetchAllLiveMatchesAsync();
            foreach (var live in liveMatches)
            {
                int idx = matches.FindIndex(m => m.Id == live.Id);
                if (idx >= 0)
                    matches[idx] = live;
                else
                    matches.Add(live);
            }
        }

        return matches;
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

    private async Task<bool> ProcessTipAsync(IMessageChannel channel, TipsMatch tip, IReadOnlyList<Match> matches)
    {
        if (!ShouldProcessTip(tip))
            return false;

        var current = matches.FirstOrDefault(m => m.Id == tip.FixtureId!.Value);

        if (current == null)
        {
            bool wasLive = tip.StatusShort is "1H" or "2H" or "HT" or "ET" or "LIVE";
            if (wasLive)
            {
                current = await _api.FetchFixtureByIdAsync(tip.FixtureId!.Value);
                if (current == null)
                {
                    _logger.Log($"Fixture {tip.FixtureId} (tip #{tip.Number}) not found even by ID", ConsoleColor.DarkYellow);
                    return false;
                }
            }
            else
            {
                _logger.Log($"Fixture {tip.FixtureId} (tip #{tip.Number}) not found", ConsoleColor.DarkYellow);
                return false;
            }
        }

        tip.Match = current;
        tip.StatusShort = current.Status.Short;
        StoreLeagueInfo(current);

        await FetchLineupsIfDueAsync(tip, current);

        if (ShouldSkipStatus(current.Status.Short))
        {
            _tipsConfig.SaveToJson();
            if (_loggedSkips.Add(tip.FixtureId!.Value))
            {
                string kickoff = tip.KickoffUtc?.ToLocalTime().ToString("dd-MM HH:mm") ?? "";
                _logger.Log($"Match #{tip.Number,-2}  {tip.HomeTeam} - {tip.AwayTeam}  {current.Status.Long}  {kickoff}", ConsoleColor.DarkYellow);
            }
            return false;
        }

        _loggedSkips.Remove(tip.FixtureId!.Value);

        LogPolledMatch(tip, current);

        tip.HomeTeamId ??= current.HomeTeamId;
        tip.AwayTeamId ??= current.AwayTeamId;
        tip.KickoffUtc = current.Date.ToUniversalTime();

        if (IsFinishedStatus(current.Status.Short))
        {
            await _announcer.ProcessMatchUpdateAsync(channel, tip);
            HandleFinishedMatch(tip, current);
            return true;
        }

        tip.Elapsed = current.Elapsed > 0 ? current.Elapsed : tip.Elapsed;
        tip.Extra   = current.Extra > 0 ? current.Extra : 0;
        _tipsConfig.SaveToJson();

        await _announcer.ProcessMatchUpdateAsync(channel, tip);
        return true;
    }

    // Lineups are typically published 60-75 min before kickoff and never change once
    // the match is underway, so this fetches once and caches forever (HomeLineup != null
    // short-circuits all future checks). Throttled per fixture so the lookahead window
    // doesn't hammer the API every 15s tick while waiting for them to be published.
    private async Task FetchLineupsIfDueAsync(TipsMatch tip, Match current)
    {
        if (tip.HomeLineup != null || !tip.FixtureId.HasValue)
            return;

        if (DateTime.UtcNow < current.Date.ToUniversalTime() - LineupLookaheadWindow)
            return;

        int fixtureId = tip.FixtureId.Value;
        if (_lastLineupCheck.TryGetValue(fixtureId, out var lastCheck) &&
            DateTime.UtcNow - lastCheck < LineupCheckInterval)
            return;

        _lastLineupCheck[fixtureId] = DateTime.UtcNow;

        var (home, away) = await _api.FetchLineupsAsync(fixtureId);
        if (home == null || away == null)
            return;

        tip.HomeLineup = home;
        tip.AwayLineup = away;
        _logger.Log($"Fetched lineups for tip #{tip.Number} ({tip.HomeTeam} vs {tip.AwayTeam})", ConsoleColor.Green);
        _tipsConfig.SaveToJson();
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

        foreach (var tip in tipsToBackfill)
            await BackfillTipAsync(tip);

        // Always persist — BackfillComplete may have flipped even when no new events were added,
        // and that flag must survive a restart or we'd keep re-fetching finished matches forever.
        _tipsConfig.Data.Events.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
        _tipsConfig.SaveToJson();
    }

    public async Task<int> BackfillTipAsync(TipsMatch tip)
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

        var (match, matchEvents) = await _api.FetchFixtureWithEventsAsync(tip.FixtureId.Value);

        if (match == null)
        {
            _logger.Log($"Backfill skipped for tip #{tip.Number}: fixture {tip.FixtureId} not found, will retry next startup", ConsoleColor.DarkYellow);
            return 0;
        }

        var couponEvents = BuildBackfilledEvents(tip, matchEvents)
            .Where(e => !existingKeys.Contains(e.Key))
            .Where(e => !existingFingerprints.Contains($"{e.FixtureId}|{e.Type}|{e.TeamId}|{e.PlayerId}|{e.Elapsed}|{e.Extra}"))
            .ToList();

        if (tip.Statistics == null)
        {
            var stats = await _api.FetchMatchStatisticsAsync(tip.FixtureId.Value);
            if (stats != null)
                tip.Statistics = stats;
        }

        if (tip.HomeLineup == null)
        {
            var (home, away) = await _api.FetchLineupsAsync(tip.FixtureId.Value);
            if (home != null && away != null)
            {
                tip.HomeLineup = home;
                tip.AwayLineup = away;
            }
        }

        // The fixture fetch succeeded for a finished match — its data won't change again,
        // so skip it on every future startup regardless of whether new events were found.
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
            VenueName = match.VenueName,
        };
    }

    private static bool ShouldSkipStatus(string status) =>
        status is "NS" or "TBD" or "HT" or "ET";

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

    private static void UpdateTipScore(TipsMatch tip, Match current)
    {
        tip.LastHomeGoals = current.HomeGoals;
        tip.LastAwayGoals = current.AwayGoals;
        tip.HomeScore = current.HomeGoals;
        tip.AwayScore = current.AwayGoals;
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
