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
    private static readonly TimeSpan PaidPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinFreeTierInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxFreeTierInterval = TimeSpan.FromMinutes(10);
    private const int FreeTierReserveCalls = 10;

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

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    public async Task StartPollingAsync(DiscordSocketClient client)
    {
        await InitializeAsync(client);

        _ = Task.Run(RunHeartbeatLoopAsync);

        if (_api.IsFreeTier)
            await RunFreeTierPollingLoopAsync(client);
        else
            await RunFixedIntervalPollingLoopAsync(client);
    }

    // Skild från själva match-pollningen så att webben kan avgöra att boten kör även
    // under free-tier-lägets långa tysta intervaller mellan matchkontroller.
    private async Task RunHeartbeatLoopAsync()
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        do
        {
            tipsConfig.WriteHeartbeat();
        } while (await timer.WaitForNextTickAsync());
    }

    private async Task RunFixedIntervalPollingLoopAsync(DiscordSocketClient client)
    {
        using var timer = new PeriodicTimer(PaidPollInterval);

        while (await timer.WaitForNextTickAsync())
            await RunPollTickAsync(client);
    }

    // Det finns inga webhooks/push för mål på API-Footballs gratisnivå — varje "kolla om
    // något hänt" är ett anrop i sig, så vi kan inte bli notifierade utan att redan ha
    // förbrukat anropet. Det smartaste vi kan göra istället för en gissad konstant är att
    // sprida dagens kvarvarande kvot över den tid som faktiskt återstår av matcherna: kort
    // intervall när det finns gott om kvot kvar och matcherna snart är klara, längre när
    // kvoten börjar ta slut eller matcherna drar ut på tiden.
    private async Task RunFreeTierPollingLoopAsync(DiscordSocketClient client)
    {
        while (true)
        {
            await Task.Delay(ComputeFreeTierDelay());
            await RunPollTickAsync(client);
        }
    }

    private TimeSpan ComputeFreeTierDelay()
    {
        var remainingWindow = EstimateRemainingLiveWindow();
        int budget = Math.Max((_api.DailyRemaining ?? 20) - FreeTierReserveCalls, 1);

        var interval = remainingWindow / budget;
        if (interval < MinFreeTierInterval) return MinFreeTierInterval;
        if (interval > MaxFreeTierInterval) return MaxFreeTierInterval;
        return interval;
    }

    // Uppskattar hur länge dagens ännu inte avslutade matcher rimligen har kvar, baserat på
    // kickoff + en generös buffer (ordinarie tid + tilläggstid + marginal för ev. förlängning/
    // straffar). Golvas så intervallet inte rusar mot noll om en match drar ut extra länge.
    private TimeSpan EstimateRemainingLiveWindow()
    {
        var now = DateTime.UtcNow;
        var estimatedFinishes = tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue && !t.IsFinished && t.KickoffUtc.HasValue)
            .Select(t => t.KickoffUtc!.Value.AddMinutes(150))
            .Where(finish => finish > now)
            .ToList();

        if (estimatedFinishes.Count == 0)
            return MinFreeTierInterval;

        var remaining = estimatedFinishes.Max() - now;
        return remaining < TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : remaining;
    }

    private async Task InitializeAsync(DiscordSocketClient client)
    {
        await _api.FetchAndApplyAccountStatusAsync();
        await fixtureMapper.InitializeFixtureIdsAsync();
        await eventBackfill.BackfillMissingEventsAsync();

        if (_api.IsFreeTier)
        {
            _logger.Log("Free tier: hoppar över H2H och skador, bara matchanrop görs", ConsoleColor.DarkCyan);
        }
        else
        {
            await FetchAndStoreInjuriesAsync();
            await FetchAndStoreH2HAsync();
        }

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
            // Posta bara om dashboarden vid uppstart om den inte redan är kanalens senaste
            // meddelande — annars blir den kvar högre upp i kanalen istället för att hamna
            // längst ner igen. Ligger den redan sist räcker det att redigera den på plats.
            string message = statusMessageService.Generate(tipsConfig.Data.MetaData.Player);
            await dashboardService.RepostIfNotLastAsync(channel, message);
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

                return CouponEvent.Create(
                    "Injury", text,
                    key: $"injury|{injury.FixtureId}|{injury.PlayerId}", fixtureId: injury.FixtureId, detail: injury.PlayerType,
                    teamId: injury.TeamId, team: teamName, playerId: injury.PlayerId, player: injury.PlayerName, comments: injury.Reason);
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

            if (tip.H2HChecked) continue;

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
            tip.H2HChecked = true;
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
