namespace PlingBot.Services;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

// Fyller i events (mål/kort/byten) för matcher som redan är klara när boten
// startar om, så historiken blir komplett även för matcher som avslutades
// medan boten var nere.
public class EventBackfillService
{
    private readonly FootballApiClient _api;
    private readonly TipsConfig tipsConfig;
    private readonly Logger _logger;

    public EventBackfillService(FootballApiClient api, TipsConfig tipsConfig, Logger logger)
    {
        _api = api;
        this.tipsConfig = tipsConfig;
        _logger = logger;
    }

    public async Task BackfillMissingEventsAsync()
    {
        var tipsToBackfill = tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue &&
                        // Kolla om matcher som redan markerats klara av en äldre version av
                        // metoden, innan Statistics/lineups lades till i backfillen — annars
                        // hoppas de över för alltid och får aldrig den nyare datan.
                        (!t.BackfillComplete || t.Statistics == null || t.HomeLineup == null) &&
                        (t.IsFinished || (t.Match != null && MatchStatus.IsFinished(t.Match.Status.Short))))
            .ToList();

        if (tipsToBackfill.Count == 0)
            return;

        var ids = tipsToBackfill.Select(t => t.FixtureId!.Value).ToList();
        var batchResults = await _api.FetchCouponFixturesBatchAsync(ids);
        var resultMap = batchResults.ToDictionary(r => r.Match.Id);

        foreach (var tip in tipsToBackfill)
        {
            if (!resultMap.TryGetValue(tip.FixtureId!.Value, out var result))
            {
                _logger.Log($"Backfill skipped for tip #{tip.Number}: fixture {tip.FixtureId} not found, will retry next startup", ConsoleColor.DarkYellow);
                continue;
            }
            BackfillTip(tip, result);
        }

        // Spara alltid — BackfillComplete kan ha blivit satt även om inga nya events
        // hittades, och den flaggan måste överleva en omstart, annars hämtar vi om
        // avslutade matcher i all oändlighet.
        tipsConfig.Data.Events.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
        tipsConfig.SaveToJson();
    }

    private int BackfillTip(TipsMatch tip, FixtureBatchResult result)
    {
        if (!tip.FixtureId.HasValue)
            return 0;

        var existingForFixture = tipsConfig.Data.Events
            .Where(e => e.FixtureId == tip.FixtureId.Value)
            .ToList();

        var existingKeys = existingForFixture.Select(e => e.Key).ToHashSet();

        // Sekundärt fingeravtryck: fångar dubbletter där live-flödet och backfillen
        // använde olika nyckelformat.
        var existingFingerprints = existingForFixture
            .Select(e => $"{e.FixtureId}|{e.Type}|{e.TeamId}|{e.PlayerId}|{e.Elapsed}|{e.Extra}")
            .ToHashSet();

        var couponEvents = BuildBackfilledEvents(tip, result.Events)
            .Where(e => !existingKeys.Contains(e.Key))
            .Where(e => !existingFingerprints.Contains($"{e.FixtureId}|{e.Type}|{e.TeamId}|{e.PlayerId}|{e.Elapsed}|{e.Extra}"))
            .ToList();

        tip.Statistics ??= result.Statistics;

        if (tip.HomeLineup == null && result.HomeLineup != null && result.AwayLineup != null)
        {
            tip.HomeLineup = result.HomeLineup;
            tip.AwayLineup = result.AwayLineup;
        }

        // Batch-hämtningen lyckades — datan ändras inte igen, så hoppa över vid varje
        // framtida uppstart oavsett om nya events hittades eller inte.
        tip.BackfillComplete = true;

        if (couponEvents.Count == 0)
            return 0;

        foreach (var ev in couponEvents)
            tipsConfig.Data.Events.Add(ev);

        _logger.Log($"Backfilled {couponEvents.Count} events for tip #{tip.Number} ({tip.HomeTeam} vs {tip.AwayTeam})", ConsoleColor.Green);
        return couponEvents.Count;
    }

    internal static List<CouponEvent> BuildBackfilledEvents(TipsMatch tip, List<MatchEvent> events)
    {
        var result = new List<CouponEvent>();
        int home = 0, away = 0;

        var filtered = events
            .Where(e => e.Elapsed <= 90) // Exkludera förlängning — kupongen räknar bara ordinarie 90 minuter
            .Where(e =>
                (e.Type == "Goal" && !string.Equals(e.Detail, "Missed Penalty", StringComparison.OrdinalIgnoreCase)) ||
                (e.Type == "Card" && (string.Equals(e.Detail, "Red Card", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(e.Detail, "Yellow Red Card", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(e.Detail, "Yellow Card", StringComparison.OrdinalIgnoreCase))) ||
                (e.Type == "Var" && (string.Equals(e.Detail, "Goal cancelled", StringComparison.OrdinalIgnoreCase) ||
                                     (e.Detail != null && e.Detail.StartsWith("Goal Disallowed", StringComparison.OrdinalIgnoreCase)))) ||
                string.Equals(e.Type, "subst", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Helpers.GetEventSortValue);

        foreach (var e in filtered)
        {
            string minute = Helpers.GetMinute(e);
            DateTime approxTime = tip.KickoffUtc?.AddMinutes(e.Elapsed + e.Extra) ?? DateTime.UtcNow;
            CouponEvent? couponEvent = null;

            if (e.Type == "Goal")
            {
                bool isOwnGoal  = string.Equals(e.Detail, "Own Goal", StringComparison.OrdinalIgnoreCase);
                bool isPenalty  = string.Equals(e.Detail, "Penalty",  StringComparison.OrdinalIgnoreCase);
                bool scorerIsHome = e.TeamId == tip.HomeTeamId;

                if (isOwnGoal) { if (scorerIsHome) away++; else home++; }
                else           { if (scorerIsHome) home++; else away++; }

                string currentSymbol = home > away ? "1" : home < away ? "2" : "X";
                string symbol = Helpers.GetEventSymbol(tip, currentSymbol);
                string score  = Helpers.FormatScore(home, away, isOwnGoal ? !scorerIsHome : scorerIsHome);
                string detail = isOwnGoal ? " (Självmål)" : isPenalty ? " (Straff)" : "";
                string player = !string.IsNullOrWhiteSpace(e.Player) ? $" - {e.Player}{detail}" : detail;

                couponEvent = new CouponEvent
                {
                    Key        = $"{e.FixtureId}-Goal-{e.Elapsed}-{e.Extra}-{e.PlayerId}",
                    Type       = "Goal",
                    FixtureId  = e.FixtureId,
                    Detail     = e.Detail,
                    TeamId     = e.TeamId,
                    Team       = scorerIsHome ? tip.HomeTeam : tip.AwayTeam,
                    Elapsed    = e.Elapsed,
                    Extra      = e.Extra,
                    Score      = $"{home}-{away}",
                    Text       = $"⚽ {symbol} Mål! {tip.HomeTeam} {score} {tip.AwayTeam} {minute}{player}",
                    PlayerId   = e.PlayerId,
                    Player     = e.Player,
                    AssistId   = e.AssistId,
                    Assist     = e.Assist,
                    CreatedUtc = approxTime
                };
            }
            else if (e.Type == "Card")
            {
                bool isHome   = e.TeamId == tip.HomeTeamId;
                string team   = isHome ? tip.HomeTeam : tip.AwayTeam;
                string currentSymbol = home > away ? "1" : home < away ? "2" : "X";
                string symbol = Helpers.GetEventSymbol(tip, currentSymbol, team, isHomeEvent: isHome, isBadEvent: true);
                string player = !string.IsNullOrWhiteSpace(e.Player) ? $" - {e.Player}" : "";
                bool isYellow = string.Equals(e.Detail, "Yellow Card", StringComparison.OrdinalIgnoreCase);
                string cardEmoji = isYellow ? "🟨" : "🟥";
                string cardLabel = isYellow ? "Gult kort!" : "Rött kort!";

                couponEvent = new CouponEvent
                {
                    Key        = $"{e.FixtureId}-Card-{e.Elapsed}-{e.Extra}-{e.PlayerId}",
                    Type       = "Card",
                    FixtureId  = e.FixtureId,
                    Detail     = e.Detail,
                    TeamId     = e.TeamId,
                    Team       = team,
                    Elapsed    = e.Elapsed,
                    Extra      = e.Extra,
                    Score      = "",
                    Text       = $"{cardEmoji} {symbol} {cardLabel} {team}{player} {minute}",
                    PlayerId   = e.PlayerId,
                    Player     = e.Player,
                    Comments   = e.Comments,
                    CreatedUtc = approxTime
                };
            }
            else if (string.Equals(e.Type, "subst", StringComparison.OrdinalIgnoreCase))
            {
                bool isHome = e.TeamId == tip.HomeTeamId;
                string team = isHome ? tip.HomeTeam : tip.AwayTeam;
                string playerOut = e.Player ?? "?";
                string playerIn  = e.Assist ?? "?";

                couponEvent = new CouponEvent
                {
                    Key        = $"{e.FixtureId}-Subst-{e.Elapsed}-{e.Extra}-{e.PlayerId}",
                    Type       = "Substitution",
                    FixtureId  = e.FixtureId,
                    Detail     = e.Detail,
                    TeamId     = e.TeamId,
                    Team       = team,
                    Elapsed    = e.Elapsed,
                    Extra      = e.Extra,
                    Score      = $"{home}-{away}",
                    Text       = $"🔄 Byte: {team} · UT {playerOut} IN {playerIn} {minute}",
                    PlayerId   = e.PlayerId,
                    Player     = e.Player,
                    AssistId   = e.AssistId,
                    Assist     = e.Assist,
                    CreatedUtc = approxTime
                };
            }
            else if (e.Type == "Var")
            {
                bool isHome   = e.TeamId == tip.HomeTeamId;
                string team   = isHome ? tip.HomeTeam : tip.AwayTeam;
                string currentSymbol = home > away ? "1" : home < away ? "2" : "X";
                string symbol = Helpers.GetEventSymbol(tip, currentSymbol, team, isHomeEvent: isHome, isBadEvent: true);
                string score  = Helpers.FormatScore(home, away, isHome);
                string player = !string.IsNullOrWhiteSpace(e.Player) ? $" - {e.Player}" : "";

                couponEvent = new CouponEvent
                {
                    Key        = $"{e.FixtureId}-Var-{e.Elapsed}-{e.Extra}-{e.PlayerId}",
                    Type       = "CancelledGoal",
                    FixtureId  = e.FixtureId,
                    Detail     = e.Detail,
                    TeamId     = e.TeamId,
                    Team       = team,
                    Elapsed    = e.Elapsed,
                    Extra      = e.Extra,
                    Score      = $"{home}-{away}",
                    Text       = $"⚠️ {symbol} Mål bortdömt! {tip.HomeTeam} {score} {tip.AwayTeam} {minute}{player}",
                    PlayerId   = e.PlayerId,
                    Player     = e.Player,
                    Comments   = e.Comments,
                    CreatedUtc = approxTime
                };
            }

            if (couponEvent != null)
                result.Add(couponEvent);
        }

        return result;
    }
}
