namespace PlingBot.Services;

using Discord;
using Discord.WebSocket;
using System;
using System.Linq;
using System.Threading.Tasks;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class TestService
{
    private readonly TipsConfig _tipsConfig;
    private readonly Logger _logger;

    public TestService(TipsConfig tipsConfig, Logger logger)
    {
        _tipsConfig = tipsConfig;
        _logger = logger;
    }

    public async Task RunAsync(DiscordSocketClient client)
    {
        Console.WriteLine();
        Console.WriteLine("=== TEST MODE ACTIVE ===");
        Console.WriteLine("Commands:");
        Console.WriteLine("  goalhome    = send fake home goal for tip #1");
        Console.WriteLine("  goalaway    = send fake away goal for tip #1");
        Console.WriteLine("  cancelhome  = send fake cancelled home goal for tip #1");
        Console.WriteLine("  cancelaway  = send fake cancelled away goal for tip #1");
        Console.WriteLine("  redhome     = send fake home red card for tip #1");
        Console.WriteLine("  redaway     = send fake away red card for tip #1");
        Console.WriteLine("  list        = list loaded tips");
        Console.WriteLine("  q           = quit test console");
        Console.WriteLine();

        var channelIdRaw = Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID_TEST");
        if (!ulong.TryParse(channelIdRaw, out var channelId))
        {
            _logger.Error("DISCORD_CHANNEL_ID_TEST missing or invalid");
            return;
        }

        IMessageChannel? channel = null;

        for (int i = 0; i < 10 && channel == null; i++)
        {
            channel = client.GetChannel(channelId) as IMessageChannel;
            if (channel == null)
                await Task.Delay(500);
        }

        if (channel == null)
        {
            _logger.Error("Test channel not found");
            return;
        }

        while (true)
        {
            Console.Write("Test command: ");
            var cmd = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(cmd))
                continue;

            if (cmd == "q")
                break;

            if (cmd == "list")
            {
                foreach (var tip in _tipsConfig.TipsMatches)
                    Console.WriteLine($"#{tip.Number}: {tip.HomeTeam} - {tip.AwayTeam} (FixtureId: {tip.FixtureId})");

                continue;
            }

            var tip1 = _tipsConfig.TipsMatches.OrderBy(t => t.Number).FirstOrDefault();
            if (tip1 == null)
            {
                Console.WriteLine("No tips loaded.");
                continue;
            }

            switch (cmd)
            {
                case "goalhome":
                    await SendFakeGoalAsync(channel, tip1, true);
                    break;

                case "goalaway":
                    await SendFakeGoalAsync(channel, tip1, false);
                    break;

                case "cancelhome":
                    await SendFakeGoalCancelledAsync(channel, tip1, true);
                    break;

                case "cancelaway":
                    await SendFakeGoalCancelledAsync(channel, tip1, false);
                    break;

                case "redhome":
                    await SendFakeRedCardAsync(channel, tip1, true);
                    break;

                case "redaway":
                    await SendFakeRedCardAsync(channel, tip1, false);
                    break;

                default:
                    Console.WriteLine("Unknown command.");
                    break;
            }
        }
    }

    private async Task SendFakeGoalAsync(IMessageChannel channel, TipsMatch tip, bool isHome)
    {
        var match = tip.Match ?? new Match
        {
            Id = tip.FixtureId ?? 999999,
            HomeTeam = tip.HomeTeam,
            AwayTeam = tip.AwayTeam,
            Status = "First Half",
            HomeGoals = tip.HomeScore,
            AwayGoals = tip.AwayScore,
            Elapsed = 42
        };

        var simulatedMatch = match with
        {
            HomeGoals = isHome ? tip.HomeScore + 1 : tip.HomeScore,
            AwayGoals = isHome ? tip.AwayScore : tip.AwayScore + 1,
            Elapsed = 42
        };

        string symbol = AnnouncementService.GetEventSymbol(tip, simulatedMatch.Symbol);

        string score = isHome
            ? $"**{simulatedMatch.HomeGoals}** - {simulatedMatch.AwayGoals}"
            : $"{simulatedMatch.HomeGoals} - **{simulatedMatch.AwayGoals}**";

        string msg = $"⚽ {symbol} Mål! {tip.HomeTeam} {score} {tip.AwayTeam} ({simulatedMatch.Elapsed}')";
        await channel.SendMessageAsync(msg);
        _logger.Log($"[TEST] Goal announced: {msg}", ConsoleColor.Magenta);

        tip.HomeScore = simulatedMatch.HomeGoals;
        tip.AwayScore = simulatedMatch.AwayGoals;
        tip.LastHomeGoals = simulatedMatch.HomeGoals;
        tip.LastAwayGoals = simulatedMatch.AwayGoals;
        tip.LastUpdatedUtc = DateTime.UtcNow;
        tip.Match = simulatedMatch;

        _tipsConfig.SaveToJson();
    }

    private async Task SendFakeGoalCancelledAsync(IMessageChannel channel, TipsMatch tip, bool isHome)
    {
        int newHomeScore = tip.HomeScore;
        int newAwayScore = tip.AwayScore;

        if (isHome)
            newHomeScore = Math.Max(0, tip.HomeScore - 1);
        else
            newAwayScore = Math.Max(0, tip.AwayScore - 1);

        var match = tip.Match ?? new Match
        {
            Id = tip.FixtureId ?? 999999,
            HomeTeam = tip.HomeTeam,
            AwayTeam = tip.AwayTeam,
            Status = "Second Half",
            HomeGoals = tip.HomeScore,
            AwayGoals = tip.AwayScore,
            Elapsed = 70
        };

        var simulatedMatch = match with
        {
            HomeGoals = newHomeScore,
            AwayGoals = newAwayScore,
            Elapsed = 70
        };

        string symbol = isHome
            ? AnnouncementService.GetEventSymbol(tip, simulatedMatch.Symbol, simulatedMatch.HomeTeam, true, true)
            : AnnouncementService.GetEventSymbol(tip, simulatedMatch.Symbol, simulatedMatch.AwayTeam, false, true);

        string score = isHome
            ? $"**{simulatedMatch.HomeGoals}** - {simulatedMatch.AwayGoals}"
            : $"{simulatedMatch.HomeGoals} - **{simulatedMatch.AwayGoals}**";

        string msg = $"⚠️ {symbol} Mål bortdömt! {tip.HomeTeam} {score} {tip.AwayTeam} ({simulatedMatch.Elapsed}')";
        await channel.SendMessageAsync(msg);
        _logger.Log($"[TEST] Cancelled goal announced: {msg}", ConsoleColor.Red);

        tip.HomeScore = newHomeScore;
        tip.AwayScore = newAwayScore;
        tip.LastHomeGoals = newHomeScore;
        tip.LastAwayGoals = newAwayScore;
        tip.LastUpdatedUtc = DateTime.UtcNow;
        tip.Match = simulatedMatch;

        _tipsConfig.SaveToJson();
    }

    private async Task SendFakeRedCardAsync(IMessageChannel channel, TipsMatch tip, bool isHome)
    {
        var match = tip.Match ?? new Match
        {
            Id = tip.FixtureId ?? 999999,
            HomeTeam = tip.HomeTeam,
            AwayTeam = tip.AwayTeam,
            Status = "Second Half",
            HomeGoals = tip.HomeScore,
            AwayGoals = tip.AwayScore,
            Elapsed = 67
        };

        var fakeEvent = new MatchEvent
        {
            Type = "Card",
            Detail = "Red Card",
            Team = isHome ? tip.HomeTeam : tip.AwayTeam,
            Player = "Testspelare",
            Elapsed = 67,
            Extra = 0
        };

        var eventKey = Helpers.BuildEventKey(fakeEvent);
        if (tip.AnnouncedEventKeys.Contains(eventKey))
        {
            fakeEvent.Elapsed++;
            eventKey = Helpers.BuildEventKey(fakeEvent);
        }

        tip.AnnouncedEventKeys.Add(eventKey);

        string symbol = isHome
            ? AnnouncementService.GetEventSymbol(tip, match.Symbol, match.HomeTeam, true, true)
            : AnnouncementService.GetEventSymbol(tip, match.Symbol, match.AwayTeam, false, true);

        string team = isHome ? tip.HomeTeam : tip.AwayTeam;
        string msg = $"🟥 {symbol} Rött kort! {team} – {fakeEvent.Player} ({fakeEvent.Elapsed})";

        await channel.SendMessageAsync(msg);
        _logger.Log($"[TEST] Red card announced: {msg}", ConsoleColor.DarkRed);

        tip.LastUpdatedUtc = DateTime.UtcNow;
        tip.Match = match with { Elapsed = fakeEvent.Elapsed };
        _tipsConfig.SaveToJson();
    }
}