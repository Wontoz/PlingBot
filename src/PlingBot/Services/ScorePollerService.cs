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
    private static readonly TimeSpan PollStartBuffer = TimeSpan.FromMinutes(2);
    private const int FixtureLookupDaysForward = 7;
    private const string ChannelEnvKey = "DISCORD_CHANNEL_ID_PROD"; // DISCORD_CHANNEL_ID_TEST FOR TEST

    private readonly FootballApiClient _api;
    private readonly AnnouncementService _announcer;
    private readonly TipsConfig _tipsConfig;
    private readonly Logger _logger;
    private readonly BotOptions _options;
    private readonly TestService _testService;
    private readonly DashboardService _dashboardService;
    private readonly StatusMessageService _statusMessageService;

    public ScorePollerService(FootballApiClient api, AnnouncementService announcer, TipsConfig tipsConfig, Logger logger, BotOptions options, TestService testService, DashboardService dashboardService, StatusMessageService statusMessageService)
    {
        _api = api;
        _announcer = announcer;
        _tipsConfig = tipsConfig;
        _logger = logger;
        _options = options;
        _testService = testService;
        _dashboardService = dashboardService;
        _statusMessageService = statusMessageService;
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
            if (ShouldPollNow())
                await CheckScoresAsync(client);
            else
                _logger.Log("No matches live or starting soon — skipping API poll", ConsoleColor.DarkGray);

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
        if (_tipsConfig.TipsMatches.All(t => t.FixtureId.HasValue))
        {
            _logger.Log("All fixture IDs already mapped — skipping fixture lookup", ConsoleColor.Green);
            return;
        }

        var allMatches = await FetchMatchesForNextDaysAsync(FixtureLookupDaysForward);

        _logger.Log($"Mapping {_tipsConfig.TipsMatches.Count} tips to {allMatches.Count} fixtures across {FixtureLookupDaysForward + 1} days", ConsoleColor.Blue);

        int mapped = 0;
        int failed = 0;

        foreach (var tip in _tipsConfig.TipsMatches.Where(t => !t.FixtureId.HasValue))
        {
            var match = FindMatchForTip(allMatches, tip);

            if (match == null)
            {
                _logger.Log($"Failed to map tip #{tip.Number,-2} ({tip.HomeKey} vs {tip.AwayKey})", ConsoleColor.DarkRed);
                failed++;
                continue;
            }

            tip.FixtureId = match.Id;
            tip.Match = match;

            _logger.Log($"Mapped tip #{tip.Number,-2} → fixture {match.Id} ({match.HomeTeam} vs {match.AwayTeam}) {match.Date:yyyy-MM-dd HH:mm}", ConsoleColor.Green);
            mapped++;
        }

        _tipsConfig.SaveToJson();
        _logger.Log($"Mapping complete: {mapped} ok, {failed} failed", ConsoleColor.Cyan);
    }

    private async Task<List<Match>> FetchMatchesForNextDaysAsync(int daysForward)
    {
        var allMatches = new List<Match>();

        for (int i = 0; i <= daysForward; i++)
        {
            var date = DateTime.UtcNow.Date.AddDays(i);
            var matchesForDate = await _api.FetchMatchesByDateAsync(date);

            _logger.Log($"Fetched {matchesForDate.Count} fixtures for {date:yyyy-MM-dd}", ConsoleColor.DarkBlue);
            allMatches.AddRange(matchesForDate);
        }

        return allMatches;
    }

    private async Task SyncInitialScoresAsync()
    {
        _logger.Log("Initial sync: scores", ConsoleColor.Blue);

        var matches = await _api.FetchTodaysMatchesAsync();

        foreach (var tip in _tipsConfig.TipsMatches.Where(t => t.FixtureId.HasValue))
        {
            var current = matches.FirstOrDefault(m => m.Id == tip.FixtureId!.Value);

            if (current == null)
            {
                _logger.Log($"No initial data for fixture {tip.FixtureId} (tip #{tip.Number})", ConsoleColor.DarkRed);
                continue;
            }

            UpdateTipScore(tip, current);
            tip.Match = current;

            _logger.Log($"Initial sync tip #{tip.Number}: {current.HomeGoals}-{current.AwayGoals} ({current.Status})", ConsoleColor.DarkCyan);
        }

        _tipsConfig.SaveToJson();
    }

    private async Task CheckScoresAsync(DiscordSocketClient client)
    {
        var channel = GetChannel(client);
        if (channel == null)
            return;

        var matches = await _api.FetchTodaysMatchesAsync();
        _logger.Log("-----------------------------------------------------------------------", ConsoleColor.DarkYellow);

        foreach (var tip in _tipsConfig.TipsMatches)
            await ProcessTipAsync(channel, tip, matches);
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

        if (ShouldSkipStatus(current.Status))
        {
            _logger.Log($"Fixture {tip.FixtureId} in {current.Status} – skipping", ConsoleColor.DarkYellow);
            return;
        }

        LogPolledMatch(tip, current);

        tip.Match = current;

        if (IsFinishedStatus(current.Status))
        {
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

    private bool ShouldPollNow()
    {
        DateTime now = DateTime.UtcNow;

        return _tipsConfig.TipsMatches.Any(tip =>
            tip.FixtureId.HasValue &&
            !tip.IsFinished &&
            tip.Match != null &&
            tip.Match.Date <= now.Add(PollStartBuffer));
    }

    private static bool ShouldProcessTip(TipsMatch tip)
    {
        return tip.FixtureId.HasValue && !tip.IsFinished;
    }

    private static bool ShouldSkipStatus(string status)
    {
        return status is "Extra Time";
    }

    private static bool IsFinishedStatus(string status)
    {
        return status is "Match Finished" or "Finished";
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
        _logger.Log($"Polling tip #{tip.Number,-2}: {tip.HomeTeam} - {tip.AwayTeam} {current.HomeGoals}-{current.AwayGoals} ({current.Status}, {current.Elapsed}')", ConsoleColor.DarkYellow);
    }
}