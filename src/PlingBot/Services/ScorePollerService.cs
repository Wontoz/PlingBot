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
    private readonly AnnouncementService _announcer;
    private readonly TipsConfig _tipsConfig;
    private readonly Logger _logger;
    private readonly BotOptions _options;
    private readonly TestService _testService;

    public ScorePollerService(
        FootballApiClient api,
        AnnouncementService announcer,
        TipsConfig tipsConfig,
        Logger logger,
        BotOptions options,
        TestService testService)
    {
        _api = api;
        _announcer = announcer;
        _tipsConfig = tipsConfig;
        _logger = logger;
        _options = options;
        _testService = testService;
    }

    public async Task StartPollingAsync(DiscordSocketClient client)
    {
        await InitializeFixtureIdsAsync();
        await SyncInitialScoresAsync();

        StartTestModeIfEnabled(client);

        var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                await CheckScoresAsync(client);
            }
            catch (Exception ex)
            {
                _logger.Error($"Polling error: {ex.Message}");
            }
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
        var allMatches = await FetchMatchesForNextDaysAsync(3);

        _logger.Log(
            $"Mapping {_tipsConfig.TipsMatches.Count} tips to {allMatches.Count} fixtures across 4 days",
            ConsoleColor.Blue);

        int mapped = 0;
        int failed = 0;

        foreach (var tip in _tipsConfig.TipsMatches)
        {
            if (tip.FixtureId.HasValue)
                continue;

            var match = FindMatchForTip(allMatches, tip);

            if (match == null)
            {
                _logger.Log(
                    $"Failed to map tip #{tip.Number,-2} ({tip.HomeKey} vs {tip.AwayKey})",
                    ConsoleColor.DarkRed);
                failed++;
                continue;
            }

            tip.FixtureId = match.Id;
            _logger.Log(
                $"Mapped tip #{tip.Number,-2} → fixture {match.Id} ({match.HomeTeam} vs {match.AwayTeam})",
                ConsoleColor.Green);
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

            _logger.Log(
                $"Fetched {matchesForDate.Count} fixtures for {date:yyyy-MM-dd}",
                ConsoleColor.DarkBlue);

            allMatches.AddRange(matchesForDate);
        }

        return allMatches;
    }

    private static Match? FindMatchForTip(IEnumerable<Match> matches, TipsMatch tip)
    {
        return matches.FirstOrDefault(m =>
            string.Equals(m.HomeTeam, tip.HomeKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.AwayTeam, tip.AwayKey, StringComparison.OrdinalIgnoreCase));
    }

    private async Task CheckScoresAsync(DiscordSocketClient client)
    {
        var channel = GetChannel(client);
        if (channel == null)
            return;

        var matches = await _api.FetchTodaysMatchesAsync();
        _logger.Log("-----------------------------------------------------------------------", ConsoleColor.DarkYellow);

        foreach (var tip in _tipsConfig.TipsMatches)
        {
            if (!tip.FixtureId.HasValue || tip.IsFinished)
                continue;

            var current = matches.FirstOrDefault(m => m.Id == tip.FixtureId.Value);
            if (current == null)
            {
                _logger.Log($"Fixture {tip.FixtureId} (tip #{tip.Number}) not found", ConsoleColor.DarkYellow);
                continue;
            }

            if (current.Status == "Extra Time")
            {
                _logger.Log($"Fixture {tip.FixtureId} in Extra Time – skipping", ConsoleColor.DarkYellow);
                continue;
            }

            _logger.Log(
                $"Polling tip #{tip.Number,-2}: {tip.HomeTeam} - {tip.AwayTeam} {current.HomeGoals}-{current.AwayGoals} ({current.Status}, {current.Elapsed}')",
                ConsoleColor.DarkYellow);

            tip.Match = current;

            if (IsFinishedStatus(current.Status))
            {
                HandleFinishedMatch(tip, current);
                continue;
            }

            await _announcer.ProcessMatchUpdateAsync(channel, tip);
        }
    }

    private IMessageChannel? GetChannel(DiscordSocketClient client)
    {
        var channelIdRaw = Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID_TEST");
        if (!ulong.TryParse(channelIdRaw, out var channelId))
        {
            _logger.Error("DISCORD_CHANNEL_ID_PROD missing or invalid");
            return null;
        }

        var channel = client.GetChannel(channelId) as IMessageChannel;
        if (channel == null)
            _logger.Error("Discord channel not found");

        return channel;
    }

    private void HandleFinishedMatch(TipsMatch tip, Match current)
    {
        tip.LastHomeGoals = current.HomeGoals;
        tip.LastAwayGoals = current.AwayGoals;
        tip.HomeScore = current.HomeGoals;
        tip.AwayScore = current.AwayGoals;
        tip.LastUpdatedUtc = DateTime.UtcNow;
        tip.IsFinished = true;

        _tipsConfig.SaveToJson();
    }

    private static bool IsFinishedStatus(string status)
    {
        return status is "Match Finished" or "Finished";
    }

    private async Task SyncInitialScoresAsync()
    {
        _logger.Log("Initial sync: scores", ConsoleColor.Blue);

        var matches = await _api.FetchTodaysMatchesAsync();

        foreach (var tip in _tipsConfig.TipsMatches)
        {
            if (!tip.FixtureId.HasValue)
                continue;

            var current = matches.FirstOrDefault(m => m.Id == tip.FixtureId.Value);
            if (current == null)
            {
                _logger.Log($"No initial data for fixture {tip.FixtureId} (tip #{tip.Number})", ConsoleColor.DarkRed);
                continue;
            }

            tip.LastHomeGoals = current.HomeGoals;
            tip.LastAwayGoals = current.AwayGoals;
            tip.HomeScore = current.HomeGoals;
            tip.AwayScore = current.AwayGoals;
            tip.Match = current;

            _logger.Log(
                $"Initial sync tip #{tip.Number}: {current.HomeGoals}-{current.AwayGoals} ({current.Status})",
                ConsoleColor.DarkCyan);
        }

        _tipsConfig.SaveToJson();
    }
}