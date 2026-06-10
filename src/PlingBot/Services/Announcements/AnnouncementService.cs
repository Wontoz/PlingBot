namespace PlingBot.Services;

using Discord;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class AnnouncementService
{
    private static readonly TimeSpan MatchEventsCacheTtl = TimeSpan.FromMinutes(1);

    private readonly FootballApiClient _api;
    private readonly TipsConfig _tipsConfig;
    private readonly CouponEvaluator _evaluator;
    private readonly Logger _logger;
    private readonly GoalAnnouncementService _goals;
    private readonly CardAnnouncementService _cards;
    private readonly Dictionary<int, (DateTime FetchedUtc, List<MatchEvent> Events)> _matchEventsCache = new();

    public AnnouncementService(
        FootballApiClient api,
        TipsConfig tipsConfig,
        CouponEvaluator evaluator,
        Logger logger,
        GoalAnnouncementService goals,
        CardAnnouncementService cards)
    {
        _api = api;
        _tipsConfig = tipsConfig;
        _evaluator = evaluator;
        _logger = logger;
        _goals = goals;
        _cards = cards;
    }

    public async Task ProcessMatchUpdateAsync(IMessageChannel channel, TipsMatch tip)
    {
        var match = tip.Match ?? throw new ArgumentNullException(nameof(tip.Match));

        bool isLive = IsLiveStatus(match.Status.Short);
        bool scoreChanged = match.HomeGoals != tip.LastHomeGoals || match.AwayGoals != tip.LastAwayGoals;

        if (!scoreChanged && !isLive)
            return;

        bool somethingHappened = false;
        var matchEvents = await FetchMatchEventsCachedAsync(match.Id, forceRefresh: scoreChanged);

        bool goalEventsHandled = await _goals.TryHandleNewGoalEventsAsync(channel, tip, match, matchEvents);
        if (goalEventsHandled)
            somethingHappened = true;

        bool cancelledGoalsHandled = await _goals.TryHandleCancelledGoalEventsAsync(channel, tip, match, matchEvents);
        if (cancelledGoalsHandled)
            somethingHappened = true;

        if (scoreChanged)
        {
            if (AnnouncementEventKeys.HasGoalBeenAdded(tip, match) && !goalEventsHandled)
                await _goals.AnnounceScoreChangeFallbackAsync(channel, tip, match);

            UpdateScore(tip, match);
            ReEvaluateCoupon();
            somethingHappened = true;
        }

        if (isLive && await _cards.AnnounceRedCardsAsync(channel, tip, match, matchEvents))
            somethingHappened = true;

        if (somethingHappened)
        {
            tip.LastUpdatedUtc = DateTime.UtcNow;
            _tipsConfig.SaveToJson();
        }
    }

    private static void UpdateScore(TipsMatch tip, Match match)
    {
        tip.LastHomeGoals = match.HomeGoals;
        tip.LastAwayGoals = match.AwayGoals;
        tip.HomeScore = match.HomeGoals;
        tip.AwayScore = match.AwayGoals;
    }

    private static bool IsLiveStatus(string status)
    {
        return status.Equals("1H", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("2H", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("LIVE", StringComparison.OrdinalIgnoreCase);
    }

    private void ReEvaluateCoupon()
    {
        var (correct, evaluated) = _evaluator.Evaluate(_tipsConfig.TipsMatches);
        _tipsConfig.Data.MetaData.TotalCorrect = correct;
        _logger.Log($"Re-evaluated coupon: {correct}/{evaluated} correct", ConsoleColor.Green);
    }

    private async Task<List<MatchEvent>> FetchMatchEventsCachedAsync(int matchId, bool forceRefresh)
    {
        if (!forceRefresh &&
            _matchEventsCache.TryGetValue(matchId, out var cached) &&
            DateTime.UtcNow - cached.FetchedUtc < MatchEventsCacheTtl)
        {
            return cached.Events;
        }

        var events = await _api.FetchMatchEventsAsync(matchId);
        _matchEventsCache[matchId] = (DateTime.UtcNow, events);
        return events;
    }
}
