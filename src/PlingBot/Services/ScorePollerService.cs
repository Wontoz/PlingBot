namespace PlingBot.Services;
using Discord;
using Discord.WebSocket;
using System;
using System.Linq;
using System.Threading.Tasks;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class ScorePollerService
{
    private readonly FootballApiClient _api;
    private readonly AnnouncementService _announcer;
    private readonly TipsConfig _tipsConfig;
    private readonly Logger _logger;

    public ScorePollerService(
        FootballApiClient api,
        AnnouncementService announcer,
        TipsConfig tipsConfig,
        Logger logger)
    {
        _api = api;
        _announcer = announcer;
        _tipsConfig = tipsConfig;
        _logger = logger;
    }
    private async Task InitializeFixtureIdsAsync()
    {
        var todayMatches = await _api.FetchTodaysMatchesAsync();

        _logger.Log($"Starting fixture mapping — { _tipsConfig.TipsMatches.Count } tips loaded from JSON", ConsoleColor.Blue);
        foreach (var tip in _tipsConfig.TipsMatches)
        { 
            // Method for testing of JSON save functionality, uncomment when needed
            // if(tip.Number == 1) SimulateGoal(tip);

            if (tip.FixtureId.HasValue) continue;

            var match = todayMatches.FirstOrDefault(m =>
                string.Equals(m.HomeTeam, tip.HomeKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.AwayTeam, tip.AwayKey, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                tip.FixtureId = match.Id;
                _logger.Log($"Mapped Tip #{tip.Number} to fixture {match.Id}", ConsoleColor.Green);
            }
            else
            {
                _logger.Error($"Could not map Tip #{tip.Number} ({tip.HomeKey} vs {tip.AwayKey})");
            }
        }

        _logger.Log($"After mapping: { _tipsConfig.TipsMatches.Count(t => t.FixtureId.HasValue) } / { _tipsConfig.TipsMatches.Count } tips have fixture IDs", ConsoleColor.Green);
    }


    public async Task StartPollingAsync(DiscordSocketClient client)
    {
        await InitializeFixtureIdsAsync(); 
        await SyncInitialScoresAsync();
        
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

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

    private async Task CheckScoresAsync(DiscordSocketClient client)
    {
        if (client.GetChannel(ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID_PROD") ?? "0")) is not IMessageChannel channel)
        {
            _logger.Error("Channel not found");
            return;
        }

        var matches = await _api.FetchTodaysMatchesAsync();
        _logger.Log($"Fetched {matches.Count} live matches", ConsoleColor.DarkBlue);

        foreach (var tip in _tipsConfig.TipsMatches)
        {
            if (!tip.FixtureId.HasValue)
                continue;

            // Find match in today's matches
            var current = matches.FirstOrDefault(m => m.Id == tip.FixtureId.Value);

            if (current == null)
            {
                _logger.Log(
                    $"Fixture {tip.FixtureId} not found in today's matches",
                    ConsoleColor.DarkRed
                );
                continue;
            }

            // Log to confirm freshness
            _logger.Log(
                $"Fresh poll for tip #{tip.Number} (fid {tip.FixtureId}): " +
                $"{current.HomeGoals}-{current.AwayGoals} ({current.Status}, {current.Elapsed} min)",
                ConsoleColor.Yellow
            );

            tip.Match = current;
            await _announcer.ProcessMatchUpdateAsync(channel, tip);
        }
    }

    private async Task SyncInitialScoresAsync()
    {
        _logger.Log("Performing initial score sync for all mapped tips", ConsoleColor.Blue);
        var matches = await _api.FetchTodaysMatchesAsync();

        foreach (var tip in _tipsConfig.TipsMatches)
        {
            if (!tip.FixtureId.HasValue) continue;

            var current = matches.FirstOrDefault(m => m.Id == tip.FixtureId.Value);
            if (current == null)
            {
                _logger.Log($"Could not fetch initial state for fixture {tip.FixtureId}", ConsoleColor.DarkRed);
                continue;
            }

            if (tip.HomeScore == 0 && tip.AwayScore == 0) // Only sync if untouched
            {
                tip.HomeScore = current.HomeGoals;
                tip.AwayScore = current.AwayGoals;
                _logger.Log($"Initial sync for tip #{tip.Number}: {tip.HomeScore}-{tip.AwayScore}", ConsoleColor.DarkCyan);
            }
            else
            {
                _logger.Log($"Tip #{tip.Number} already has stored scores ({tip.HomeScore}-{tip.AwayScore}) – skipping initial sync", ConsoleColor.DarkGray);
            }
        }

        _tipsConfig.SaveToJson();
    }

    // Simulate a goal to test JSON saving functionality
    private void SimulateGoal(TipsMatch tip)
    {
        tip.HomeScore = 99;
        _logger.Log("TEST: Faking goal for tip " + tip.Number, ConsoleColor.Magenta);

        // If in AnnouncementService:
        // await AnnounceGoalAsync(...);
        // then the save should happen here ↓
        _tipsConfig.SaveToJson();

        _logger.Log("TEST: Fake goal saved to json", ConsoleColor.Green);
    }
}