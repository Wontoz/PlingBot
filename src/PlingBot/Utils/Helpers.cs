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
}
