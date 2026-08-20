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

    private readonly FootballApiClient _api;
    private readonly AnnouncementService announcer;
    private readonly TipsConfig tipsConfig;
    private readonly Logger _logger;
    private readonly DashboardService dashboardService;
    private readonly PlayerMessageService statusMessageService;
    private readonly CouponPercentageService couponPercentageService;
    private readonly PayoutScraperService payoutScraper;
    private readonly FixtureMappingService fixtureMapper;
    private readonly EventBackfillService eventBackfill;
    private readonly HashSet<int> loggedSkips = new();
    private bool payoutCheckedAtRoundStart = false;

    public ScorePollerService(
        FootballApiClient api,
        AnnouncementService announcer,
        TipsConfig tipsConfig,
        Logger logger,
        DashboardService dashboardService,
        PlayerMessageService statusMessageService,
        CouponPercentageService couponPercentageService,
        PayoutScraperService payoutScraper,
        FixtureMappingService fixtureMapper,
        EventBackfillService eventBackfill)
    {
        _api = api;
        this.announcer = announcer;
        this.tipsConfig = tipsConfig;
        _logger = logger;
        this.dashboardService = dashboardService;
        this.statusMessageService = statusMessageService;
        this.couponPercentageService = couponPercentageService;
        this.payoutScraper = payoutScraper;
        this.fixtureMapper = fixtureMapper;
        this.eventBackfill = eventBackfill;
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
        await fixtureMapper.InitializeFixtureIdsAsync();
        await eventBackfill.BackfillMissingEventsAsync();
        await FetchAndStoreInjuriesAsync();
        await FetchAndStoreH2HAsync();
        await couponPercentageService.RefreshIfDueAsync();
        await SyncInitialScoresAsync();

        // Mål/avslutnings-triggad hämtning av utdelning körs bara under live-polling, så en bot
        // som startar (eller startas om) efter att omgången redan är klar skulle annars aldrig
        // försöka hämta den alls. Fånga det fallet här en gång vid uppstart — men bara när HELA
        // omgången är klar, eftersom utdelning inte kan finnas medan någon match fortfarande
        // pågår och vi bara skulle bränna retry-fönstret på ett garanterat tomt försök.
        bool roundFullyFinished = tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue)
            .All(t => t.IsFinished);

        if (tipsConfig.Data.MetaData.Payouts.Count == 0 && roundFullyFinished)
            payoutScraper.ScheduleUpdate();

        // Om StartTime redan har passerat när vi startar har kontrollen vid omgångsstart
        // missats — markera den som klar så vi inte triggar den igen på första poll-ticket.
        var startTime = tipsConfig.Data.MetaData.StartTime;
        if (startTime.HasValue && DateTime.UtcNow >= startTime.Value)
            payoutCheckedAtRoundStart = true;

        var channel = GetChannel(client);
        if (channel != null)
        {
            // Ta bort ev. gammalt dashboard-meddelande och posta ett nytt vid uppstart,
            // precis som !refresh gör — annars blir det kvar högre upp i kanalen istället
            // för att hamna längst ner igen.
            string message = statusMessageService.Generate(tipsConfig.Data.MetaData.Player);
            await dashboardService.DeletePreviousDashboardsAsync(channel);
            await dashboardService.CreateOrUpdateAsync(channel, message);
        }

    }

    private async Task RunPollTickAsync(DiscordSocketClient client)
    {
        try
        {
            await couponPercentageService.RefreshIfDueAsync();

            if (!payoutCheckedAtRoundStart)
            {
                var startTime = tipsConfig.Data.MetaData.StartTime;
                if (startTime.HasValue && DateTime.UtcNow >= startTime.Value)
                {
                    payoutCheckedAtRoundStart = true;
                    if (tipsConfig.Data.MetaData.Payouts.Count == 0)
                        payoutScraper.ScheduleUpdate();
                }
            }

            await CheckScoresAsync(client);

            dashboardService.RefreshExtraMessageIfNeeded(statusMessageService);
            await dashboardService.UpdateIfExistsAsync(client);
        }
        catch (Exception ex)
        {
            _logger.Error($"Polling error: {ex.Message}");
        }
    }

    private async Task FetchAndStoreInjuriesAsync()
    {
        var tipsToFetch = tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue && !t.IsFinished)
            .ToList();

        if (tipsToFetch.Count == 0)
            return;

        var ids = tipsToFetch.Select(t => t.FixtureId!.Value).ToList();
        var injuries = await _api.FetchInjuriesAsync(ids);

        // Migrera: ta bort eventuella gamla injury-events som låg i den delade Events-listan
        tipsConfig.Data.Events.RemoveAll(e => e.Type == "Injury");

        var tipByFixture = tipsToFetch.ToDictionary(t => t.FixtureId!.Value);

        // Rensa skador för alla hämtade tips så att tillfrisknade spelare försvinner
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

        tipsConfig.SaveToJson();
        _logger.Log($"Injuries: {injuries.Count} fetched across {byFixture.Count()} fixtures", ConsoleColor.Cyan);
    }

    private async Task FetchAndStoreH2HAsync()
    {
        bool any = false;
        foreach (var tip in tipsConfig.TipsMatches)
        {
            if (!tip.HomeTeamId.HasValue || !tip.AwayTeamId.HasValue)
                continue;

            // Hämta om igen om den saknas eller om cachad data är från innan lag-ID stöddes (alla ID:n är 0)
            bool needsFetch = tip.H2H == null || tip.H2H.All(f => f.HomeTeamId == 0 && f.AwayTeamId == 0);
            if (!needsFetch) continue;

            _logger.Log($"H2H #{tip.Number}: {tip.HomeTeam} vs {tip.AwayTeam}", ConsoleColor.DarkCyan);
            var fixtures = await _api.FetchHeadToHeadAsync(tip.HomeTeamId.Value, tip.AwayTeamId.Value);

            // Ersätt API:ets lagnamn med kupongens visningsnamn
            foreach (var f in fixtures)
            {
                if (f.HomeTeamId == tip.HomeTeamId) f.HomeTeam = tip.HomeTeam;
                else if (f.HomeTeamId == tip.AwayTeamId) f.HomeTeam = tip.AwayTeam;

                if (f.AwayTeamId == tip.AwayTeamId) f.AwayTeam = tip.AwayTeam;
                else if (f.AwayTeamId == tip.HomeTeamId) f.AwayTeam = tip.HomeTeam;
            }

            tip.H2H = fixtures;
            any = true;
        }
        if (any) tipsConfig.SaveToJson();
    }

    private async Task SyncInitialScoresAsync()
    {

        var ids = tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue)
            .Select(t => t.FixtureId!.Value)
            .ToList();

        if (ids.Count == 0)
            return;

        var batchResults = await _api.FetchCouponFixturesBatchAsync(ids);
        var resultMap = batchResults.ToDictionary(r => r.Match.Id);

        foreach (var tip in tipsConfig.TipsMatches.Where(t => t.FixtureId.HasValue))
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
            LeagueInfoWriter.Store(tipsConfig, current);

        }

        tipsConfig.SaveToJson();
    }

    private async Task CheckScoresAsync(DiscordSocketClient client)
    {
        var channel = GetChannel(client);
        if (channel == null || !HasMatchesInPlay())
            return;

        var ids = tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue && !t.IsFinished)
            .Select(t => t.FixtureId!.Value)
            .ToList();

        if (ids.Count == 0)
            return;

        var batchResults = await _api.FetchCouponFixturesBatchAsync(ids);
        var resultMap = batchResults.ToDictionary(r => r.Match.Id);

        bool anyPolled = false;
        foreach (var tip in tipsConfig.TipsMatches)
            anyPolled |= await ProcessTipAsync(channel, tip, resultMap);

        if (anyPolled)
            _logger.Log("-----------------------------------------------------------------------", ConsoleColor.DarkYellow);
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
        LeagueInfoWriter.Store(tipsConfig, current);

        // Lineups kommer med i varje batch-svar; spara dem första gången de dyker upp.
        if (tip.HomeLineup == null && result.HomeLineup != null && result.AwayLineup != null)
        {
            tip.HomeLineup = result.HomeLineup;
            tip.AwayLineup = result.AwayLineup;
            _logger.Log($"Fetched lineups for tip #{tip.Number} ({tip.HomeTeam} vs {tip.AwayTeam})", ConsoleColor.Green);
            tipsConfig.SaveToJson();
        }

        tip.KickoffUtc = current.Date.ToUniversalTime();

        if (MatchStatus.ShouldSkip(current.Status.Short))
        {
            tipsConfig.SaveToJson();
            if (loggedSkips.Add(tip.FixtureId.Value))
            {
                string kickoff = tip.KickoffUtc.Value.ToLocalTime().ToString("dd-MM HH:mm");
                _logger.Log($"Match #{tip.Number,-2}  {tip.HomeTeam} - {tip.AwayTeam}  {current.Status.Long}  {kickoff}", ConsoleColor.DarkYellow);
            }
            return false;
        }

        loggedSkips.Remove(tip.FixtureId.Value);
        LogPolledMatch(tip, current);

        tip.HomeTeamId ??= current.HomeTeamId;
        tip.AwayTeamId ??= current.AwayTeamId;

        if (MatchStatus.IsFinished(current.Status.Short))
        {
            // AET/PEN = matchen avgjord i förlängning eller straffar — mål gjorda i
            // förlängning/straffar får inte annonseras (förlängning är medvetet utanför
            // scope för live-annonseringar).
            bool isExtraTimeFinish = current.Status.Short.Equals("AET", StringComparison.OrdinalIgnoreCase) ||
                                     current.Status.Short.Equals("PEN", StringComparison.OrdinalIgnoreCase);
            if (!isExtraTimeFinish)
                await announcer.ProcessMatchUpdateAsync(channel, tip, result.Events, result.Statistics);
            HandleFinishedMatch(tip, current);
            return true;
        }

        tip.Elapsed = current.Elapsed > 0 ? current.Elapsed : tip.Elapsed;
        tip.Extra   = current.Extra > 0 ? current.Extra : 0;
        tipsConfig.SaveToJson();

        await announcer.ProcessMatchUpdateAsync(channel, tip, result.Events, result.Statistics);
        return true;
    }

    private IMessageChannel? GetChannel(DiscordSocketClient client)
    {
        var channelId = DiscordChannel.ResolveAllowedChannelId();
        if (channelId == null)
        {
            _logger.Error($"{DiscordChannel.EnvKey} missing or invalid");
            return null;
        }

        var channel = client.GetChannel(channelId.Value) as IMessageChannel;

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
        return tipsConfig.TipsMatches.Any(tip =>
            tip.FixtureId.HasValue &&
            !tip.IsFinished &&
            tip.KickoffUtc.HasValue &&
            tip.KickoffUtc.Value <= now);
    }

    private static bool ShouldProcessTip(TipsMatch tip)
    {
        return tip.FixtureId.HasValue && !tip.IsFinished;
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

        tipsConfig.SaveToJson();
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
