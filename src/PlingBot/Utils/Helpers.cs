using System;
using System.Text.Json;
using PlingBot.Models;

namespace PlingBot.Utils;

public static class Helpers
{
    public static string BuildEventKey(MatchEvent ev)
    {
        return $"{ev.Type}|{ev.Detail}|{ev.Team}|{ev.Player}|{ev.Elapsed}|{ev.ExtraTime}";
    }

    public static string FormatScore(int homeGoals, int awayGoals, bool highlightHome)
    {
        return highlightHome
            ? $"**{homeGoals}** - {awayGoals}"
            : $"{homeGoals} - **{awayGoals}**";
    }

    public static int GetEventSortValue(MatchEvent ev)
    {
        return ev.Elapsed * 100 + ev.ExtraTime;
    }
}