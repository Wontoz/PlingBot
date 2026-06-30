using Discord;
using PlingBot.Models;

namespace PlingBot.Utils;

public static class Helpers
{
    public static string GetEventSymbol(TipsMatch tip, string matchSymbol, string? team = null, bool? isHomeEvent = null, bool isBadEvent = false)
    {
        if (tip.Tip == "1X2")
            return "✅";

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

    public static string ClassifyGoalBenefit(string tipStr, bool homeScored, int homeGoals, int awayGoals)
    {
        if (tipStr == "1X2") return "✅";

        int prevHome = homeScored ? homeGoals - 1 : homeGoals;
        int prevAway = homeScored ? awayGoals : awayGoals - 1;

        string prevOutcome = GetOutcome(prevHome, prevAway);
        string newOutcome  = GetOutcome(homeGoals, awayGoals);

        bool newGood = tipStr.Contains(newOutcome);
        bool scorerHelps = (homeScored  && tipStr.Contains("1")) ||
                           (!homeScored && tipStr.Contains("2")) ||
                           (tipStr.Contains("X") && ((homeScored  && prevOutcome == "2") ||
                                                     (!homeScored && prevOutcome == "1")));

        if ( newGood &&  scorerHelps) return "✅";
        if ( newGood && !scorerHelps) return "🟠";
        if (!newGood &&  scorerHelps) return "🎯";
        return "❌";
    }

    private static string GetOutcome(int home, int away) => home > away ? "1" : home < away ? "2" : "X";
    public static string BuildEventKey(MatchEvent ev)
    {
        return $"{ev.Type}|{ev.Detail}|{ev.Team}|{ev.Player}|{ev.Elapsed}|{ev.Extra}";
    }

    public static string BuildScoreTransitionKey(Match match, int oldHomeGoals, int oldAwayGoals)
    {
        return $"score|{match.Id}|{oldHomeGoals}-{oldAwayGoals}|{match.HomeGoals}-{match.AwayGoals}";
    }

    public static string FormatScore(int homeGoals, int awayGoals, bool highlightHome)
    {
        return highlightHome
            ? $"**{homeGoals}** - {awayGoals}"
            : $"{homeGoals} - **{awayGoals}**";
    }
    public static string GetMinute(Match match)
    {
        if (match.Elapsed <= 0)
            return string.Empty;

        return $"({match.Elapsed}')";
    }

    public static string GetMinute(MatchEvent ev)
    {
        if (ev.Elapsed <= 0)
            return string.Empty;

        return ev.Extra > 0
            ? $"({ev.Elapsed}+{ev.Extra}')"
            : $"({ev.Elapsed}')";
    }

    public static int GetEventSortValue(MatchEvent ev)
    {
        return ev.Elapsed * 100 + ev.Extra;
    }

    public static void DeleteAfterDelay(TimeSpan delay, params IUserMessage[] messages)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            foreach (var msg in messages)
            {
                try { await msg.DeleteAsync(); }
                catch { }
            }
        });
    }
}
