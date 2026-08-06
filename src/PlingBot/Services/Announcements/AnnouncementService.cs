namespace PlingBot.Services;

using System.Linq;
using Discord;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class AnnouncementService
{
    private readonly TipsConfig tipsConfig;
    private readonly CouponEvaluator evaluator;
    private readonly Logger _logger;
    private readonly GoalAnnouncementService goals;
    private readonly CardAnnouncementService cards;
    private readonly PayoutScraperService payoutScraper;

    public AnnouncementService(
        TipsConfig tipsConfig,
        CouponEvaluator evaluator,
        Logger logger,
        GoalAnnouncementService goals,
        CardAnnouncementService cards,
        PayoutScraperService payoutScraper)
    {
        this.tipsConfig = tipsConfig;
        this.evaluator = evaluator;
        _logger = logger;
        this.goals = goals;
        this.cards = cards;
        this.payoutScraper = payoutScraper;
    }

    // events och stats hämtas i förväg av anroparen via ett enda batch-API-anrop —
    // inga interna API-anrop görs här längre.
    public async Task ProcessMatchUpdateAsync(
        IMessageChannel channel,
        TipsMatch tip,
        List<MatchEvent> matchEvents,
        MatchStatistics? stats)
    {
        var match = tip.Match ?? throw new ArgumentNullException(nameof(tip.Match));

        bool isLive = MatchStatus.IsLive(match.Status.Short);
        bool isHalftime = match.Status.Short.Equals("HT", StringComparison.OrdinalIgnoreCase);
        bool scoreChanged = match.HomeGoals != tip.LastHomeGoals || match.AwayGoals != tip.LastAwayGoals;

        // I halvtid kan datan fortfarande ändras (t.ex. assists som läggs till av API:et) —
        // tillåt lagring av uppdateringar men isLive förblir false så Discord-annonser
        // ändå hålls tillbaka.
        if (!scoreChanged && !isLive && !isHalftime)
            return;

        bool somethingHappened = false;

        bool goalEventsHandled = await goals.TryHandleNewGoalEventsAsync(channel, tip, match, matchEvents);
        if (goalEventsHandled)
            somethingHappened = true;

        bool cancelledGoalsHandled = await goals.TryHandleCancelledGoalEventsAsync(channel, tip, match, matchEvents);
        if (cancelledGoalsHandled)
            somethingHappened = true;

        if (scoreChanged)
        {
            if (AnnouncementEventKeys.HasGoalBeenAdded(tip, match) && !goalEventsHandled)
                await goals.AnnounceScoreChangeFallbackAsync(channel, tip, match);

            UpdateScore(tip, match);
            ReEvaluateCoupon();
            payoutScraper.ScheduleUpdate();
            somethingHappened = true;
        }

        if (isLive && await cards.AnnounceRedCardsAsync(channel, tip, match, matchEvents))
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
            tipsConfig.SaveToJson();
        }

        // Utdelning brukar postas en stund efter den faktiska slutsignalen, inte direkt efter
        // sista målet — så starta även ett nytt retry-fönster i samma stund matchen tar slut,
        // inte bara vid poängändring (detta triggas bara en gång: ProcessTipAsync slutar
        // anropa den här metoden för tippet på nästa poll, när IsFinished väl blir true).
        if (MatchStatus.IsFinished(match.Status.Short))
            payoutScraper.ScheduleUpdate();
    }

    private static void UpdateScore(TipsMatch tip, Match match)
    {
        tip.LastHomeGoals = match.HomeGoals;
        tip.LastAwayGoals = match.AwayGoals;
        tip.HomeScore = match.HomeGoals;
        tip.AwayScore = match.AwayGoals;
    }

    private void ReEvaluateCoupon()
    {
        var (correct, evaluated) = evaluator.Evaluate(tipsConfig.TipsMatches);
        tipsConfig.Data.MetaData.TotalCorrect = correct;
        _logger.Log($"Re-evaluated coupon: {correct}/{evaluated} correct", ConsoleColor.Green);
    }

    // Hur mycket (i Elapsed*100+Extra-enheter) två poster får driva isär och ändå räknas
    // som samma fysiska kort/byte. API:et kan både nudga tilläggstidsminuter en tick eller
    // två (diff=1–2) OCH flytta hela den spelade minuten 1–2 när spelarnamnet väl slår in
    // (diff=100–200). 300 täcker upp till 3 hela minuters drift.
    private const int QuietEventDriftTolerance = 300;

    // Sparar tyst byten och vanliga gula kort för webbens per-match-flik —
    // annonseras aldrig till Discord och visas aldrig i det kuraterade live-flödet.
    private bool CaptureQuietEvents(TipsMatch tip, Match match, List<MatchEvent> matchEvents)
    {
        bool added = false;

        var existingForFixture = tipsConfig.Data.Events
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

            // API:et rapporterar ofta ett kort/byte i samma stund det sker utan spelare
            // ("Okänd"), och fyller sedan i namnet (och kan nudga tilläggstidsminuten) en
            // eller två pollningar senare. Att bara nyckla på spelare+minut skulle behandla
            // det som ett helt nytt event och ge en dubblett av "Okänd" + namngiven för
            // samma fysiska kort — stäm istället av mot den närmaste olösta posten för
            // samma lag/typ.
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
            tipsConfig.Data.Events.Add(couponEvent);
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
