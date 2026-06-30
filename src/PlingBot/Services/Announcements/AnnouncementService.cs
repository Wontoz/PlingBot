namespace PlingBot.Services;

using System.Linq;
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
    private readonly PayoutScraperService _payoutScraper;
    private readonly Dictionary<int, (DateTime FetchedUtc, List<MatchEvent> Events)> _matchEventsCache = new();
    private readonly Dictionary<int, (DateTime FetchedUtc, MatchStatistics? Stats)> _matchStatsCache = new();

    public AnnouncementService(
        FootballApiClient api,
        TipsConfig tipsConfig,
        CouponEvaluator evaluator,
        Logger logger,
        GoalAnnouncementService goals,
        CardAnnouncementService cards,
        PayoutScraperService payoutScraper)
    {
        _api = api;
        _tipsConfig = tipsConfig;
        _evaluator = evaluator;
        _logger = logger;
        _goals = goals;
        _cards = cards;
        _payoutScraper = payoutScraper;
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
            _payoutScraper.ScheduleUpdate();
            somethingHappened = true;
        }

        if (isLive && await _cards.AnnounceRedCardsAsync(channel, tip, match, matchEvents))
            somethingHappened = true;

        if (isLive && CaptureQuietEvents(tip, match, matchEvents))
            somethingHappened = true;

        if (isLive)
        {
            var stats = await FetchMatchStatisticsCachedAsync(match.Id, forceRefresh: scoreChanged);
            if (stats != null)
            {
                tip.Statistics = stats;
                somethingHappened = true;
            }
        }

        if (somethingHappened)
        {
            tip.LastUpdatedUtc = DateTime.UtcNow;
            _tipsConfig.SaveToJson();
        }

        // Payouts are usually posted some time after the actual final whistle, not right after
        // the last goal — so also kick off a fresh retry window the moment a match finishes,
        // not just on score changes (this only fires once: ProcessTipAsync stops calling this
        // method for the tip on the next poll, once IsFinished flips to true).
        if (IsFinishedStatus(match.Status.Short))
            _payoutScraper.ScheduleUpdate();
    }

    private static bool IsFinishedStatus(string status) =>
        status.Equals("FT", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("AET", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("PEN", StringComparison.OrdinalIgnoreCase);

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

    private async Task<MatchStatistics?> FetchMatchStatisticsCachedAsync(int matchId, bool forceRefresh)
    {
        if (!forceRefresh &&
            _matchStatsCache.TryGetValue(matchId, out var cached) &&
            DateTime.UtcNow - cached.FetchedUtc < MatchEventsCacheTtl)
        {
            return cached.Stats;
        }

        var stats = await _api.FetchMatchStatisticsAsync(matchId);
        _matchStatsCache[matchId] = (DateTime.UtcNow, stats);
        return stats;
    }

    // Quietly stores substitutions and plain yellow cards for the web's per-match
    // event tab — never announced to Discord and never shown in the curated live feed.
    private bool CaptureQuietEvents(TipsMatch tip, Match match, List<MatchEvent> matchEvents)
    {
        bool added = false;

        foreach (var ev in matchEvents.Where(IsQuietlyStoredEvent))
        {
            string key = AnnouncementEventKeys.BuildStoredEventKey("quiet", match.Id, ev);
            if (tip.AnnouncedEventKeys.Contains(key))
                continue;

            tip.AnnouncedEventKeys.Add(key);
            _tipsConfig.Data.Events.Add(BuildQuietCouponEvent(key, tip, match, ev));
            added = true;
        }

        return added;
    }

    private static bool IsQuietlyStoredEvent(MatchEvent ev) =>
        string.Equals(ev.Type, "subst", StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(ev.Type, "Card", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(ev.Detail, "Yellow Card", StringComparison.OrdinalIgnoreCase));

    private static CouponEvent BuildQuietCouponEvent(string key, TipsMatch tip, Match match, MatchEvent ev)
    {
        bool isHome = AnnouncementEventKeys.IsHomeEvent(match, ev);
        string team = isHome ? tip.HomeTeam : tip.AwayTeam;
        string minute = Helpers.GetMinute(ev);

        if (string.Equals(ev.Type, "subst", StringComparison.OrdinalIgnoreCase))
        {
            return new CouponEvent
            {
                Key = key,
                Type = "Substitution",
                FixtureId = match.Id,
                Detail = ev.Detail,
                TeamId = ev.TeamId,
                Team = team,
                Elapsed = ev.Elapsed,
                Extra = ev.Extra,
                Score = match.Score,
                Text = $"🔄 Byte: {team} · UT {ev.Player ?? "?"} IN {ev.Assist ?? "?"} {minute}",
                PlayerId = ev.PlayerId,
                Player = ev.Player,
                AssistId = ev.AssistId,
                Assist = ev.Assist,
                CreatedUtc = DateTime.UtcNow
            };
        }

        return new CouponEvent
        {
            Key = key,
            Type = "Card",
            FixtureId = match.Id,
            Detail = "Yellow Card",
            TeamId = ev.TeamId,
            Team = team,
            Elapsed = ev.Elapsed,
            Extra = ev.Extra,
            Score = match.Score,
            Text = $"🟨 Gult kort! {team} - {ev.Player ?? "Okänd spelare"} {minute}",
            PlayerId = ev.PlayerId,
            Player = ev.Player,
            Comments = ev.Comments,
            CreatedUtc = DateTime.UtcNow
        };
    }
}
