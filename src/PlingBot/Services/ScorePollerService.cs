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
    private const string ChannelEnvKey = "DISCORD_CHANNEL_ID_PROD"; // DISCORD_CHANNEL_ID_TEST FOR TEST

    private readonly FootballApiClient _api;
    private readonly AnnouncementService _announcer;
    private readonly TipsConfig _tipsConfig;
    private readonly Logger _logger;
    private readonly BotOptions _options;
    private readonly TestService _testService;
    private readonly DashboardService _dashboardService;
    private readonly PlayerMessageService _statusMessageService;
    private readonly CouponPercentageService _couponPercentageService;
    private readonly Dictionary<DateTime, (DateTime FetchedUtc, List<Match> Matches)> _fixtureDateCache = new();

    public ScorePollerService(
        FootballApiClient api,
        AnnouncementService announcer,
        TipsConfig tipsConfig,
        Logger logger,
        BotOptions options,
        TestService testService,
        DashboardService dashboardService,
        PlayerMessageService statusMessageService,
        CouponPercentageService couponPercentageService)
    {
        _api = api;
        _announcer = announcer;
        _tipsConfig = tipsConfig;
        _logger = logger;
        _options = options;
        _testService = testService;
        _dashboardService = dashboardService;
        _statusMessageService = statusMessageService;
        _couponPercentageService = couponPercentageService;
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
        await InitializeFixtureIdsAsync();
        await _couponPercentageService.RefreshIfDueAsync();
        await SyncInitialScoresAsync();

        var channel = GetChannel(client);
        if (channel != null)
        {
            string message = _statusMessageService.Generate(_tipsConfig.Data.MetaData.Player);
            await _dashboardService.RefreshOrCreateOnStartupAsync(channel, message);
        }

        StartTestModeIfEnabled(client);
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

    private void StartTestModeIfEnabled(DiscordSocketClient client)
    {
        if (!_options.TestMode)
            return;

        _logger.Log("TEST MODE enabled", ConsoleColor.Magenta);
        _ = Task.Run(() => _testService.RunAsync(client));
    }

    private async Task InitializeFixtureIdsAsync()
    {
        var unresolvedTips = _tipsConfig.TipsMatches.ToList();
        int mapped = 0;
        int loaded = 0;

        _logger.Log($"Mapping {_tipsConfig.TipsMatches.Count} tips day-by-day, max {FixtureLookupDaysForward + 1} days", ConsoleColor.Blue);

        for (int i = 0; i <= FixtureLookupDaysForward && unresolvedTips.Count > 0; i++)
        {
            var date = DateTime.UtcNow.Date.AddDays(i);
            var matchesForDate = await FetchMatchesByDateCachedAsync(date, forceRefresh: true);

            _logger.Log($"Fetched {matchesForDate.Count} fixtures for {date:yyyy-MM-dd}", ConsoleColor.DarkBlue);

            foreach (var tip in unresolvedTips.ToList())
            {
                bool alreadyMapped = tip.FixtureId.HasValue;
                var match = alreadyMapped
                    ? matchesForDate.FirstOrDefault(m => m.Id == tip.FixtureId!.Value)
                    : FindMatchForTip(matchesForDate, tip);

                if (match == null)
                    continue;

                unresolvedTips.Remove(tip);
                tip.FixtureId = match.Id;
                tip.HomeTeamId ??= match.HomeTeamId;
                tip.AwayTeamId ??= match.AwayTeamId;
                tip.Match = match;

                if (alreadyMapped)
                {
                    _logger.Log($"Loaded tip #{tip.Number,-2} fixture {match.Id} ({match.HomeTeam} vs {match.AwayTeam}) {match.Date:yyyy-MM-dd HH:mm}", ConsoleColor.Green);
                    loaded++;
                }
                else
                {
                    _logger.Log($"Mapped tip #{tip.Number,-2} -> fixture {match.Id} ({match.HomeTeam} vs {match.AwayTeam}) {match.Date:yyyy-MM-dd HH:mm}", ConsoleColor.Green);
                    mapped++;
                }
            }
        }

        foreach (var tip in unresolvedTips.OrderBy(tip => tip.Number))
            _logger.Log($"Failed to map tip #{tip.Number,-2} ({tip.HomeKey} vs {tip.AwayKey})", ConsoleColor.DarkRed);

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
            tip.Match = current;

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
        _logger.Log("-----------------------------------------------------------------------", ConsoleColor.DarkYellow);

        foreach (var tip in _tipsConfig.TipsMatches)
            await ProcessTipAsync(channel, tip, matches);
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

    private async Task ProcessTipAsync(IMessageChannel channel, TipsMatch tip, IReadOnlyList<Match> matches)
    {
        if (!ShouldProcessTip(tip))
            return;

        var current = matches.FirstOrDefault(m => m.Id == tip.FixtureId!.Value);

        if (current == null)
        {
            _logger.Log($"Fixture {tip.FixtureId} (tip #{tip.Number}) not found", ConsoleColor.DarkYellow);
            return;
        }

        if (ShouldSkipStatus(current.Status.Short))
        {
            _logger.Log($"Fixture {tip.FixtureId} in {current.Status.Short} - skipping", ConsoleColor.DarkYellow);
            return;
        }

        LogPolledMatch(tip, current);

        tip.HomeTeamId ??= current.HomeTeamId;
        tip.AwayTeamId ??= current.AwayTeamId;
        tip.Match = current;

        if (IsFinishedStatus(current.Status.Short))
        {
            await _announcer.ProcessMatchUpdateAsync(channel, tip);
            HandleFinishedMatch(tip, current);
            return;
        }

        await _announcer.ProcessMatchUpdateAsync(channel, tip);
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
            tip.Match != null &&
            tip.Match.Date.ToUniversalTime() <= now);
    }

    private static bool ShouldProcessTip(TipsMatch tip)
    {
        return tip.FixtureId.HasValue && !tip.IsFinished;
    }

    private static bool ShouldSkipStatus(string status)
    {
        return status.Equals("ET", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFinishedStatus(string status)
    {
        return status.Equals("FT", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("AET", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("PEN", StringComparison.OrdinalIgnoreCase);
    }

    private static Match? FindMatchForTip(IEnumerable<Match> matches, TipsMatch tip)
    {
        return matches.FirstOrDefault(m =>
            TeamMatches(m.HomeTeam, tip.HomeKey) &&
            TeamMatches(m.AwayTeam, tip.AwayKey));
    }

    private static bool TeamMatches(string apiTeam, string tipTeam)
    {
        return string.Equals(NormalizeTeamName(apiTeam), NormalizeTeamName(tipTeam), StringComparison.OrdinalIgnoreCase);
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
        _logger.Log($"Polling tip #{tip.Number,-2}: {tip.HomeTeam} - {tip.AwayTeam} {current.HomeGoals}-{current.AwayGoals} ({current.Status.Long}, {FormatMatchMinute(current)})", ConsoleColor.DarkYellow);
    }

    private static string FormatMatchMinute(Match match)
    {
        string minute = match.Extra > 0
            ? $"{match.Elapsed}+{match.Extra}"
            : $"{match.Elapsed}";

        return $"{minute}'";
    }
}
