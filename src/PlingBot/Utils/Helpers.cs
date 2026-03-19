using System;
using System.Text.Json;
using PlingBot.Models;

namespace PlingBot.Utils;

public static class Helpers
{
    public static string BuildEventKey(MatchEvent ev)
    {
        return $"{ev.Type}|{ev.Detail}|{ev.Team}|{ev.Player}|{ev.Elapsed}|{ev.Extra}";
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

        string minute = match.Extra > 0 ? $"{match.Elapsed}+{match.Extra}" : $"{match.Elapsed}";
        return $"({minute}')";
    }

    public static int GetEventSortValue(MatchEvent ev)
    {
        return ev.Elapsed * 100 + ev.Extra;
    }
}