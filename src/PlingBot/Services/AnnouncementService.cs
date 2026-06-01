namespace PlingBot.Services;

using Discord;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class AnnouncementService
{
    private readonly FootballApiClient _api;
    private readonly TipsConfig _tipsConfig;
    private readonly CouponEvaluator _evaluator;
    private readonly Logger _logger;
    private readonly Dictionary<int, DateTime> _lastRedCardChecks = new();
    private readonly DashboardService _dashboardService;

    public AnnouncementService(FootballApiClient api, TipsConfig tipsConfig, CouponEvaluator evaluator, Logger logger, DashboardService dashboardService)
    {
        _api = api;
        _tipsConfig = tipsConfig;
        _evaluator = evaluator;
        _logger = logger;
        _dashboardService = dashboardService;
    }

    public async Task ProcessMatchUpdateAsync(IMessageChannel channel, TipsMatch tip)
    {
        var match = tip.Match ?? throw new ArgumentNullException(nameof(tip.Match));

        bool isLive = match.Status is "First Half" or "Second Half";
        bool scoreChanged = match.HomeGoals != tip.LastHomeGoals || match.AwayGoals != tip.LastAwayGoals;

        if (!scoreChanged && !isLive)
            return;

        bool somethingHappened = false;

        if (scoreChanged)
        {
            string key = Helpers.BuildScoreTransitionKey(match, tip.LastHomeGoals, tip.LastAwayGoals);

            if (!tip.AnnouncedEventKeys.Contains(key))
            {
                tip.AnnouncedEventKeys.Add(key);
                await AnnounceScoreChangeAsync(channel, tip, match);
            }

            UpdateScore(tip, match);
            ReEvaluateCoupon();
            somethingHappened = true;
        }

        if (isLive && await AnnounceRedCardsAsync(channel, tip, match))
            somethingHappened = true;

        if (somethingHappened)
        {
            tip.LastUpdatedUtc = DateTime.UtcNow;
            _tipsConfig.SaveToJson();
        }
    }

    private async Task AnnounceScoreChangeAsync(IMessageChannel channel, TipsMatch tip, Match match)
    {
        int homeDiff = match.HomeGoals - tip.LastHomeGoals;
        int awayDiff = match.AwayGoals - tip.LastAwayGoals;

        if (homeDiff > 0)
            await AnnounceGoalAsync(channel, tip, match, true);

        if (awayDiff > 0)
            await AnnounceGoalAsync(channel, tip, match, false);

        if (homeDiff < 0)
            await AnnounceGoalCancelledAsync(channel, tip, match, true);

        if (awayDiff < 0)
            await AnnounceGoalCancelledAsync(channel, tip, match, false);
    }

    private async Task<bool> AnnounceRedCardsAsync(IMessageChannel channel, TipsMatch tip, Match match)
    {
        bool shouldCheck = !_lastRedCardChecks.TryGetValue(match.Id, out var lastCheck) || DateTime.UtcNow - lastCheck >= TimeSpan.FromMinutes(2);
        if (!shouldCheck)
            return false;

        _lastRedCardChecks[match.Id] = DateTime.UtcNow;

        var cardEvents = await _api.FetchMatchEventsByTypeAsync(match.Id, "card");
        bool announced = false;

        foreach (var ev in cardEvents
            .Where(e => string.Equals(e.Detail, "Red Card", StringComparison.OrdinalIgnoreCase) || string.Equals(e.Detail, "Second Yellow Card", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Helpers.GetEventSortValue))
        {
            string key = "card|" + match.Id + "|" + Helpers.BuildEventKey(ev);

            if (tip.AnnouncedEventKeys.Contains(key))
                continue;

            bool isHome = string.Equals(ev.Team, match.HomeTeam, StringComparison.OrdinalIgnoreCase);

            await AnnounceRedCardAsync(channel, tip, match, isHome, ev);

            tip.AnnouncedEventKeys.Add(key);
            announced = true;
        }

        return announced;
    }

    private void UpdateScore(TipsMatch tip, Match match)
    {
        tip.LastHomeGoals = match.HomeGoals;
        tip.LastAwayGoals = match.AwayGoals;
        tip.HomeScore = match.HomeGoals;
        tip.AwayScore = match.AwayGoals;
    }

    private void ReEvaluateCoupon()
    {
        var (correct, evaluated) = _evaluator.Evaluate(_tipsConfig.TipsMatches);
        _tipsConfig.Data.MetaData.TotalCorrect = correct;
        _logger.Log($"Re-evaluated coupon: {correct}/{evaluated} correct", ConsoleColor.Green);
    }

    private async Task AnnounceGoalAsync(IMessageChannel channel, TipsMatch tip, Match match, bool homeScored)
    {
        string symbol = Helpers.GetEventSymbol(tip, match.Symbol);
        string score = Helpers.FormatScore(match.HomeGoals, match.AwayGoals, homeScored);
        string msg = $"⚽ {symbol} Mål! {tip.HomeTeam} {score} {tip.AwayTeam} {Helpers.GetMinute(match)}";

        await AnnounceMessageAsync(channel, msg, ConsoleColor.Magenta, "Goal announced");
    }

    private async Task AnnounceGoalCancelledAsync(IMessageChannel channel, TipsMatch tip, Match match, bool isHome)
    {
        string symbol = isHome
            ? Helpers.GetEventSymbol(tip, match.Symbol, match.HomeTeam, isHomeEvent: true, isBadEvent: true)
            : Helpers.GetEventSymbol(tip, match.Symbol, match.AwayTeam, isHomeEvent: false, isBadEvent: true);

        string score = Helpers.FormatScore(match.HomeGoals, match.AwayGoals, isHome);
        string msg = $"⚠️ {symbol} Mål bortdömt! {tip.HomeTeam} {score} {tip.AwayTeam} {Helpers.GetMinute(match)}";
        
        await AnnounceMessageAsync(channel, msg, ConsoleColor.Red, "Cancelled goal announced");
    }

    private async Task AnnounceRedCardAsync(IMessageChannel channel, TipsMatch tip, Match match, bool isHome, MatchEvent? evt)
    {
        string team = isHome ? tip.HomeTeam : tip.AwayTeam;
        string symbol = isHome
            ? Helpers.GetEventSymbol(tip, match.Symbol, match.HomeTeam, isHomeEvent: true, isBadEvent: true)
            : Helpers.GetEventSymbol(tip, match.Symbol, match.AwayTeam, isHomeEvent: false, isBadEvent: true);

        string player = string.IsNullOrEmpty(evt?.Player) ? "Okänd spelare" : evt.Player;
        string msg = $"🟥 {symbol} Rött kort! {team} – {player} {Helpers.GetMinute(match)}";
        
        await AnnounceMessageAsync(channel, msg, ConsoleColor.DarkRed, "Red card announced");
    }

    private async Task AnnounceMessageAsync(IMessageChannel channel, string msg, ConsoleColor logColor, string logPrefix)
    {
        _dashboardService.AddEvent(msg);

        var sentMessage = await channel.SendMessageAsync(msg);
        _logger.Log($"{logPrefix}: {msg}", logColor);

        _ = DeleteMessageAsync(sentMessage, TimeSpan.FromMinutes(1));
    }

    private async Task DeleteMessageAsync(IUserMessage message, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await message.DeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.Log($"Could not delete message: {ex.Message}", ConsoleColor.DarkYellow);
        }
    }
}