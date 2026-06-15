namespace PlingBot.Services;

using PlingBot.Models;
using PlingBot.Utils;

internal static class AnnouncementEventKeys
{
    public static bool HasGoalBeenAdded(TipsMatch tip, Match match)
    {
        return match.HomeGoals + match.AwayGoals > tip.LastHomeGoals + tip.LastAwayGoals;
    }

    public static bool IsHomeEvent(Match match, MatchEvent ev)
    {
        if (ev.TeamId.HasValue && match.HomeTeamId.HasValue)
            return ev.TeamId.Value == match.HomeTeamId.Value;

        return string.Equals(ev.Team, match.HomeTeam, StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildStoredEventKey(string category, int matchId, MatchEvent ev)
    {
        return category + "|" + matchId + "|" + Helpers.BuildEventKey(ev);
    }

    public static string BuildCardKey(int matchId, int eventIndex)
    {
        return $"card|{matchId}|{eventIndex}";
    }

    public static string BuildVarKey(int matchId, int eventIndex)
    {
        return $"var|{matchId}|{eventIndex}";
    }

    public static string BuildGoalKey(int matchId, int eventIndex)
    {
        return $"goal|{matchId}|{eventIndex}";
    }
}
