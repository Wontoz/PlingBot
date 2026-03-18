namespace PlingBot.Services;

using Discord;
using System;
using System.Linq;
using System.Threading.Tasks;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class AnnouncementService
{
    private readonly FootballApiClient _api;
    private readonly TipsConfig _tipsConfig;
    private readonly Logger _logger;

    public AnnouncementService(
        FootballApiClient api,
        TipsConfig tipsConfig,
        Logger logger)
    {
        _api = api;
        _tipsConfig = tipsConfig;
        _logger = logger;
    }

    public async Task ProcessMatchUpdateAsync(IMessageChannel channel, TipsMatch tip)
    {
        var match = tip.Match ?? throw new ArgumentNullException(nameof(tip.Match));

        int homeGoalDiff = match.HomeGoals - tip.LastHomeGoals;
        int awayGoalDiff = match.AwayGoals - tip.LastAwayGoals;

        bool isLive = match.Status is "First Half" or "Second Half";
        bool scoreChanged = homeGoalDiff != 0 || awayGoalDiff != 0;

        if (!scoreChanged && !isLive)
            return;

        // Goal cancellations
        if (homeGoalDiff < 0 || awayGoalDiff < 0)
        {
            if (homeGoalDiff < 0)
            {
                int cancellations = -homeGoalDiff;

                for (int i = 0; i < cancellations; i++)
                {
                    await AnnounceGoalCancelledAsync(channel, tip, match, true);
                }
            }

            if (awayGoalDiff < 0)
            {
                int cancellations = -awayGoalDiff;

                for (int i = 0; i < cancellations; i++)
                {
                    await AnnounceGoalCancelledAsync(channel, tip, match, false);
                }
            }
        }

        // Goals
        if (homeGoalDiff > 0)
        {
            for (int i = 0; i < homeGoalDiff; i++)
                await AnnounceGoalAsync(channel, tip, match, true);
        }

        if (awayGoalDiff > 0)
        {
            for (int i = 0; i < awayGoalDiff; i++)
                await AnnounceGoalAsync(channel, tip, match, false);
        }

        // Red cards
        if (isLive)
        {
            var cardEvents = await _api.FetchMatchEventsByTypeAsync(match.Id, "card");

            foreach (var ev in cardEvents
                .Where(e =>
                    string.Equals(e.Detail, "Red Card", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Detail, "Second Yellow Card", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Helpers.GetEventSortValue))
            {
                string key = Helpers.BuildEventKey(ev);

                if (tip.AnnouncedEventKeys.Contains(key))
                    continue;

                bool isHome = string.Equals(ev.Team, match.HomeTeam, StringComparison.OrdinalIgnoreCase);

                await AnnounceRedCardAsync(channel, tip, match, isHome, ev);

                tip.AnnouncedEventKeys.Add(key);
            }
        }

        tip.LastHomeGoals = match.HomeGoals;
        tip.LastAwayGoals = match.AwayGoals;
        tip.HomeScore = match.HomeGoals;
        tip.AwayScore = match.AwayGoals;
        tip.LastUpdatedUtc = DateTime.UtcNow;

        _tipsConfig.SaveToJson();

        _logger.Log(
            $"Update tip {tip.Number}: score {match.HomeGoals}-{match.AwayGoals}",
            ConsoleColor.Cyan
        );
    }

    private async Task AnnounceGoalAsync(IMessageChannel channel, TipsMatch tip, Match match, bool homeScored)
    {
        string symbol = GetEventSymbol(tip, match.Symbol);
        string score = Helpers.FormatScore(match.HomeGoals, match.AwayGoals, homeScored);

        string msg = $"⚽ {symbol} Mål! {tip.HomeTeam} {score} {tip.AwayTeam} ({match.Elapsed}')";
        await channel.SendMessageAsync(msg);

        _logger.Log($"Goal announced: {msg}", ConsoleColor.Magenta);
    }

    private async Task AnnounceGoalCancelledAsync(IMessageChannel channel, TipsMatch tip, Match match, bool isHome)
    {
        string symbol = isHome
            ? GetEventSymbol(tip, match.Symbol, match.HomeTeam, isHomeEvent: true, isBadEvent: true)
            : GetEventSymbol(tip, match.Symbol, match.AwayTeam, isHomeEvent: false, isBadEvent: true);

        string score = Helpers.FormatScore(match.HomeGoals, match.AwayGoals, isHome);

        string msg = $"⚠️ {symbol} Mål bortdömt! {tip.HomeTeam} {score} {tip.AwayTeam} ({match.Elapsed}')";
        await channel.SendMessageAsync(msg);
        _logger.Log($"Cancelled goal announced: {msg}", ConsoleColor.Red);
    }

    private async Task AnnounceRedCardAsync(IMessageChannel channel, TipsMatch tip, Match match, bool isHome, MatchEvent? evt)
    {
        string team = isHome ? tip.HomeTeam : tip.AwayTeam;

        string symbol = isHome
            ? GetEventSymbol(tip, match.Symbol, match.HomeTeam, isHomeEvent: true, isBadEvent: true)
            : GetEventSymbol(tip, match.Symbol, match.AwayTeam, isHomeEvent: false, isBadEvent: true);

        string player = string.IsNullOrEmpty(evt?.Player) ? "Okänd spelare" : evt.Player;

        string msg = $"🟥 {symbol} Rött kort! {team} – {player} ({match.Elapsed}')";
        await channel.SendMessageAsync(msg);

        _logger.Log($"Red card announced: {msg}", ConsoleColor.DarkRed);
    }

    public static string GetEventSymbol(TipsMatch tip, string matchSymbol, string? team = null, bool? isHomeEvent = null, bool isBadEvent = false)
    {
        if (tip.Tip == "1X2")
            return "➖";

        bool isGood;

        if (team != null && isHomeEvent.HasValue)
        {
            bool teamMatchesTip =
                (isHomeEvent.Value && tip.Tip.Contains("1")) ||
                (!isHomeEvent.Value && tip.Tip.Contains("2"));

            isGood = teamMatchesTip;
        }
        else
        {
            isGood = tip.Tip.Contains(matchSymbol);
        }

        if (isBadEvent)
            isGood = !isGood;

        return isGood ? "✅" : "❌";
    }
}