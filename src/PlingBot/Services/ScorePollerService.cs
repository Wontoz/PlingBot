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

        _logger.Log($"Mapping { _tipsConfig.TipsMatches.Count } tips to today's {todayMatches.Count} fixtures", ConsoleColor.Blue);

        int mapped = 0;
        int failed = 0;

        foreach (var tip in _tipsConfig.TipsMatches)
        {
            if (tip.FixtureId.HasValue) continue;

            var match = todayMatches.FirstOrDefault(m =>
                string.Equals(m.HomeTeam, tip.HomeKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.AwayTeam, tip.AwayKey, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                tip.FixtureId = match.Id;
                _logger.Log($"Mapped tip #{tip.Number,-2} → fixture {match.Id} ({tip.HomeKey} vs {tip.AwayKey})", ConsoleColor.Green);
                mapped++;
            }
            else
            {
                _logger.Log($"Failed to map tip #{tip.Number,-2} ({tip.HomeKey} vs {tip.AwayKey})", ConsoleColor.DarkRed);
                failed++;
            }
        }

        _logger.Log($"Mapping complete: {mapped} succeeded, {failed} failed", ConsoleColor.Cyan);
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
        var channel = client.GetChannel(ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID_PROD") ?? "0")) as IMessageChannel;
        if (channel == null)
        {
            _logger.Error("Discord channel not found");
            return;
        }

        var matches = await _api.FetchTodaysMatchesAsync();
        _logger.Log($"Fetched {matches.Count} matches for today", ConsoleColor.DarkBlue);

        foreach (var tip in _tipsConfig.TipsMatches)
        {
            if (!tip.FixtureId.HasValue) continue;

            var current = matches.FirstOrDefault(m => m.Id == tip.FixtureId.Value);
            if (current == null)
            {
                _logger.Log($"Fixture {tip.FixtureId} (tip #{tip.Number}) not found in today's matches", ConsoleColor.DarkRed);
                continue;
            }

            _logger.Log(
                $"Poll tip #{tip.Number,-2} (fid {tip.FixtureId}): {current.HomeGoals}-{current.AwayGoals} ({current.Status}, {current.Elapsed}'')",
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