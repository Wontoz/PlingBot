namespace PlingBot.Services;

using System.Linq;
using Discord;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class AnnouncementService
{
    private readonly TipsConfig _tipsConfig;
    private readonly CouponEvaluator _evaluator;
    private readonly Logger _logger;
    private readonly GoalAnnouncementService _goals;
    private readonly CardAnnouncementService _cards;
    private readonly PayoutScraperService _payoutScraper;

    public AnnouncementService(
        TipsConfig tipsConfig,
        CouponEvaluator evaluator,
        Logger logger,
        GoalAnnouncementService goals,
        CardAnnouncementService cards,
        PayoutScraperService payoutScraper)
    {
        _tipsConfig = tipsConfig;
        _evaluator = evaluator;
        _logger = logger;
        _goals = goals;
        _cards = cards;
        _payoutScraper = payoutScraper;
    }

    // events and stats are pre-fetched by the caller via a single batch API call —
    // no internal API requests are made here any more.
    public async Task ProcessMatchUpdateAsync(
        IMessageChannel channel,
        TipsMatch tip,
        List<MatchEvent> matchEvents,
        MatchStatistics? stats)
    {
        var match = tip.Match ?? throw new ArgumentNullException(nameof(tip.Match));

        bool isLive = IsLiveStatus(match.Status.Short);
        bool isHalftime = match.Status.Short.Equals("HT", StringComparison.OrdinalIgnoreCase);
        bool scoreChanged = match.HomeGoals != tip.LastHomeGoals || match.AwayGoals != tip.LastAwayGoals;

        // During halftime, data can still change (e.g. assists added by API) — allow storage
        // updates but isLive remains false so Discord announcements are still suppressed.
        if (!scoreChanged && !isLive && !isHalftime)
            return;

        bool somethingHappened = false;

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

        if (stats != null)
        {
            tip.Statistics = stats;
            somethingHappened = true;
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

    // How far apart (in Elapsed*100+Extra units) two entries may drift and still be treated as
    // the same physical card/subst. The API can both nudge stoppage-time extra minutes by a
    // tick or two (diff=1–2) AND shift the whole elapsed minute by 1–2 when the player name
    // eventually resolves (diff=100–200). 300 covers up to 3 full minutes of elapsed drift.
    private const int QuietEventDriftTolerance = 300;

    // Quietly stores substitutions and plain yellow cards for the web's per-match
    // event tab — never announced to Discord and never shown in the curated live feed.
    private bool CaptureQuietEvents(TipsMatch tip, Match match, List<MatchEvent> matchEvents)
    {
        bool added = false;

        var existingForFixture = _tipsConfig.Data.Events
            .Where(e => e.FixtureId == match.Id && (e.Type == "Card" || e.Type == "Substitution"))
            .ToList();

        var resolvedFingerprints = existingForFixture
            .Where(e => !string.IsNullOrWhiteSpace(e.Player))
            .Select(e => $"{e.Type}|{e.TeamId}|{e.PlayerId}")
            .ToHashSet();

        foreach (var ev in matchEvents.Where(IsQuietlyStoredEvent))
        {
            bool isSubst = string.Equals(ev.Type, "subst", StringComparison.OrdinalIgnoreCase);
            string evType = isSubst ? "Substitution" : "Card";

            if (ev.PlayerId > 0 && resolvedFingerprints.Contains($"{evType}|{ev.TeamId}|{ev.PlayerId}"))
                continue;

            // The API often reports a card/subst the instant it happens with no player attached
            // yet ("Okänd"), then fills the name in (and can nudge the stoppage-time minute) a
            // poll or two later. Keying strictly on player+minute treats that as a brand new
            // event and produces a duplicate "Okänd" + named pair for the same physical card —
            // reconcile against the nearest still-unresolved entry for the same team/type instead.
            if (!string.IsNullOrWhiteSpace(ev.Player))
            {
                int evScore = ev.Elapsed * 100 + ev.Extra;
                var placeholder = existingForFixture
                    .Where(e => e.Type == evType && e.TeamId == ev.TeamId && string.IsNullOrWhiteSpace(e.Player))
                    .Select(e => (Entry: e, Diff: Math.Abs((e.Elapsed * 100 + e.Extra) - evScore)))
                    .Where(x => x.Diff <= QuietEventDriftTolerance)
                    .OrderBy(x => x.Diff)
                    .Select(x => x.Entry)
                    .FirstOrDefault();

                if (placeholder != null)
                {
                    var resolved = BuildQuietCouponEvent(placeholder.Key, tip, match, ev);
                    placeholder.Player = resolved.Player;
                    placeholder.PlayerId = resolved.PlayerId;
                    placeholder.Assist = resolved.Assist;
                    placeholder.AssistId = resolved.AssistId;
                    placeholder.Comments = resolved.Comments;
                    placeholder.Text = resolved.Text;
                    placeholder.Elapsed = ev.Elapsed;
                    placeholder.Extra = ev.Extra;
                    resolvedFingerprints.Add($"{evType}|{ev.TeamId}|{ev.PlayerId}");
                    added = true;
                    continue;
                }
            }

            string key = AnnouncementEventKeys.BuildStoredEventKey("quiet", match.Id, ev);
            if (tip.AnnouncedEventKeys.Contains(key))
                continue;

            tip.AnnouncedEventKeys.Add(key);
            var couponEvent = BuildQuietCouponEvent(key, tip, match, ev);
            _tipsConfig.Data.Events.Add(couponEvent);
            existingForFixture.Add(couponEvent);
            if (!string.IsNullOrWhiteSpace(couponEvent.Player))
                resolvedFingerprints.Add($"{couponEvent.Type}|{couponEvent.TeamId}|{couponEvent.PlayerId}");
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
