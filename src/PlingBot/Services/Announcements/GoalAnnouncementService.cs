namespace PlingBot.Services;

using Discord;
using PlingBot.Models;
using PlingBot.Utils;

public class GoalAnnouncementService
{
    private readonly DiscordAnnouncementService _discord;
    private readonly DashboardService _dashboardService;

    private sealed record GoalEventInfo(
        MatchEvent Event,
        int Index,
        bool IsHome,
        int HomeGoals,
        int AwayGoals,
        string Key,
        string? Player,
        string Message,
        string MessageWithoutPlayer,
        string Identity);

    public GoalAnnouncementService(DiscordAnnouncementService discord, DashboardService dashboardService)
    {
        _discord = discord;
        _dashboardService = dashboardService;
    }

    public async Task<bool> TryHandleNewGoalEventsAsync(
        IMessageChannel channel,
        TipsMatch tip,
        Match match,
        IReadOnlyList<MatchEvent> matchEvents)
    {
        int previousGoalTotal = tip.LastHomeGoals + tip.LastAwayGoals;
        var scoringGoalEvents = GetScoringGoalEvents(matchEvents);
        var candidateEvents = GetCompatibleGoalEvents(tip, match, scoringGoalEvents);

        if (candidateEvents.Count == 0)
            return false;

        bool handled = false;

        foreach (var item in candidateEvents)
        {
            if (!tip.AnnouncedEventKeys.Contains(item.Key))
            {
                if (item.Index < previousGoalTotal)
                {
                    handled |= await TryCompleteFallbackGoalAsync(channel, tip, item);
                    continue;
                }

                handled |= await AnnounceNewGoalEventAsync(channel, tip, item);
                continue;
            }

            handled |= await TryCompleteAnnouncedGoalAsync(channel, tip, item);
        }

        return handled;
    }

    public async Task<bool> AnnounceScoreChangeFallbackAsync(IMessageChannel channel, TipsMatch tip, Match match)
    {
        string key = Helpers.BuildScoreTransitionKey(match, tip.LastHomeGoals, tip.LastAwayGoals);

        if (tip.AnnouncedEventKeys.Contains(key))
            return false;

        tip.AnnouncedEventKeys.Add(key);

        int homeDiff = match.HomeGoals - tip.LastHomeGoals;
        int awayDiff = match.AwayGoals - tip.LastAwayGoals;
        bool announced = false;

        if (homeDiff > 0)
            announced |= await AnnounceAddedGoalsAsync(channel, tip, match, isHome: true, homeDiff);

        if (awayDiff > 0)
            announced |= await AnnounceAddedGoalsAsync(channel, tip, match, isHome: false, awayDiff);

        return announced;
    }

    public async Task<bool> TryHandleCancelledGoalEventsAsync(
        IMessageChannel channel,
        TipsMatch tip,
        Match match,
        IReadOnlyList<MatchEvent> matchEvents)
    {
        bool announced = false;

        foreach (var ev in matchEvents
            .Where(ev => string.Equals(ev.Type, "Var", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ev.Detail, "Goal cancelled", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Helpers.GetEventSortValue))
        {
            string key = AnnouncementEventKeys.BuildStoredEventKey("var", match.Id, ev);

            if (tip.AnnouncedEventKeys.Contains(key))
                continue;

            bool isHome = AnnouncementEventKeys.IsHomeEvent(match, ev);
            await AnnounceGoalCancelledAsync(channel, tip, match, isHome, ev, key);
            tip.AnnouncedEventKeys.Add(key);
            announced = true;
        }

        return announced;
    }

    private async Task<bool> AnnounceAddedGoalsAsync(IMessageChannel channel, TipsMatch tip, Match match, bool isHome, int goalCount)
    {
        bool announced = false;

        for (int i = 1; i <= goalCount; i++)
        {
            int goalIndex = tip.LastHomeGoals + tip.LastAwayGoals + i - 1;
            string goalKey = AnnouncementEventKeys.BuildGoalKey(match.Id, goalIndex);
            if (tip.AnnouncedEventKeys.Contains(goalKey))
                continue;

            string message = BuildGoalMessage(tip, match, isHome, null, match.HomeGoals, match.AwayGoals);
            var sentMessage = await _discord.AnnounceAsync(
                channel,
                message,
                ConsoleColor.Magenta,
                "Goal announced",
                deleteDelay: TimeSpan.FromMinutes(5),
                couponEvent: BuildScoreFallbackGoalEvent(match, isHome, message));

            tip.AnnouncedEventKeys.Add(goalKey);
            _discord.TrackGoalMessage(goalKey, sentMessage);
            announced = true;
        }

        return announced;
    }

    private async Task<bool> AnnounceNewGoalEventAsync(IMessageChannel channel, TipsMatch tip, GoalEventInfo goal)
    {
        bool playerKnown = !string.IsNullOrWhiteSpace(goal.Player);
        var sentMessage = await _discord.AnnounceAsync(
            channel,
            goal.Message,
            ConsoleColor.Magenta,
            "Goal announced",
            deleteDelay: playerKnown ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5),
            couponEvent: BuildGoalCouponEvent(goal));

        tip.AnnouncedEventKeys.Add(goal.Key);
        RemoveScoreFallbackKeyForGoal(tip, goal);

        if (!playerKnown)
            _discord.TrackGoalMessage(goal.Key, sentMessage);

        return true;
    }

    private async Task<bool> TryCompleteFallbackGoalAsync(IMessageChannel channel, TipsMatch tip, GoalEventInfo goal)
    {
        if (string.IsNullOrWhiteSpace(goal.Player))
            return false;

        bool updated = await TryUpdateGoalEverywhereAsync(channel, goal);
        if (!updated)
            return await AnnounceNewGoalEventAsync(channel, tip, goal);

        tip.AnnouncedEventKeys.Add(goal.Key);
        RemoveScoreFallbackKeyForGoal(tip, goal);
        return true;
    }

    private async Task<bool> TryCompleteAnnouncedGoalAsync(IMessageChannel channel, TipsMatch tip, GoalEventInfo goal)
    {
        if (string.IsNullOrWhiteSpace(goal.Player))
            return false;

        bool updated = await TryUpdateGoalEverywhereAsync(channel, goal);
        if (!updated)
            return false;

        RemoveScoreFallbackKeyForGoal(tip, goal);
        return true;
    }

    private async Task<bool> TryUpdateGoalEverywhereAsync(IMessageChannel channel, GoalEventInfo goal)
    {
        var couponEvent = BuildGoalCouponEvent(goal);
        bool dashboardUpdated =
            _dashboardService.UpdateEvent(goal.MessageWithoutPlayer, couponEvent) ||
            _dashboardService.UpdateEventContaining(goal.Identity, couponEvent);
        bool discordUpdated = await _discord.TryUpdateGoalMessageAsync(
            channel,
            goal.Key,
            goal.MessageWithoutPlayer,
            goal.Message,
            goal.Identity);

        return dashboardUpdated || discordUpdated;
    }

    private static List<(MatchEvent Event, int Index)> GetScoringGoalEvents(IReadOnlyList<MatchEvent> matchEvents)
    {
        return matchEvents
            .Where(IsScoringGoalEvent)
            .OrderBy(Helpers.GetEventSortValue)
            .Select((ev, index) => (ev, index))
            .ToList();
    }

    private static List<GoalEventInfo> GetCompatibleGoalEvents(
        TipsMatch tip,
        Match match,
        IReadOnlyList<(MatchEvent Event, int Index)> scoringGoalEvents)
    {
        var compatibleGoals = new List<GoalEventInfo>();
        int expectedHomeGoals = tip.LastHomeGoals;
        int expectedAwayGoals = tip.LastAwayGoals;

        foreach (var item in scoringGoalEvents)
        {
            var goal = BuildGoalEventInfo(tip, match, scoringGoalEvents, item);

            if (IsAlreadyKnownScore(tip, goal))
            {
                if (tip.AnnouncedEventKeys.Contains(goal.Key) && !string.IsNullOrWhiteSpace(goal.Player))
                    compatibleGoals.Add(goal);

                continue;
            }

            if (expectedHomeGoals == match.HomeGoals && expectedAwayGoals == match.AwayGoals)
                break;

            int nextHomeGoals = expectedHomeGoals + (goal.IsHome ? 1 : 0);
            int nextAwayGoals = expectedAwayGoals + (goal.IsHome ? 0 : 1);

            if (goal.HomeGoals != nextHomeGoals || goal.AwayGoals != nextAwayGoals)
                continue;

            if (goal.HomeGoals > match.HomeGoals || goal.AwayGoals > match.AwayGoals)
                continue;

            compatibleGoals.Add(goal);
            expectedHomeGoals = goal.HomeGoals;
            expectedAwayGoals = goal.AwayGoals;
        }

        return compatibleGoals;
    }

    private static bool IsAlreadyKnownScore(TipsMatch tip, GoalEventInfo goal)
    {
        return goal.HomeGoals <= tip.LastHomeGoals &&
            goal.AwayGoals <= tip.LastAwayGoals;
    }

    private static GoalEventInfo BuildGoalEventInfo(
        TipsMatch tip,
        Match match,
        IReadOnlyList<(MatchEvent Event, int Index)> scoringGoalEvents,
        (MatchEvent Event, int Index) item)
    {
        bool isHome = AnnouncementEventKeys.IsHomeEvent(match, item.Event);
        int homeGoals = scoringGoalEvents
            .Take(item.Index + 1)
            .Count(goal => AnnouncementEventKeys.IsHomeEvent(match, goal.Event));
        int awayGoals = item.Index + 1 - homeGoals;
        string key = AnnouncementEventKeys.BuildGoalKey(match.Id, item.Index);
        string? player = item.Event.Player;

        return new GoalEventInfo(
            item.Event,
            item.Index,
            isHome,
            homeGoals,
            awayGoals,
            key,
            player,
            BuildGoalMessage(tip, match, isHome, item.Event, homeGoals, awayGoals, player),
            BuildGoalMessage(tip, match, isHome, item.Event, homeGoals, awayGoals, includePlayer: false),
            BuildGoalIdentity(tip, isHome, homeGoals, awayGoals));
    }

    private static CouponEvent BuildGoalCouponEvent(GoalEventInfo goal)
    {
        return new CouponEvent
        {
            Key = goal.Key,
            Type = "Goal",
            FixtureId = goal.Event.FixtureId,
            Detail = goal.Event.Detail,
            TeamId = goal.Event.TeamId,
            Team = goal.Event.Team ?? "",
            Elapsed = goal.Event.Elapsed,
            Extra = goal.Event.Extra,
            Score = $"{goal.HomeGoals}-{goal.AwayGoals}",
            Text = goal.Message,
            PlayerId = goal.Event.PlayerId,
            Player = goal.Player,
            AssistId = goal.Event.AssistId,
            Assist = goal.Event.Assist,
            CreatedUtc = DateTime.UtcNow
        };
    }

    private static CouponEvent BuildScoreFallbackGoalEvent(Match match, bool isHome, string message)
    {
        return new CouponEvent
        {
            Key = AnnouncementEventKeys.BuildGoalKey(match.Id, match.HomeGoals + match.AwayGoals - 1),
            Type = "Goal",
            FixtureId = match.Id,
            Detail = "Score Fallback",
            TeamId = isHome ? match.HomeTeamId : match.AwayTeamId,
            Team = isHome ? match.HomeTeam : match.AwayTeam,
            Elapsed = match.Elapsed,
            Extra = match.Extra,
            Score = match.Score,
            Text = message,
            CreatedUtc = DateTime.UtcNow
        };
    }

    private static void RemoveScoreFallbackKeyForGoal(TipsMatch tip, GoalEventInfo goal)
    {
        string scoreKey = BuildScoreFallbackKey(tip.FixtureId, goal);
        if (!string.IsNullOrWhiteSpace(scoreKey))
            tip.AnnouncedEventKeys.Remove(scoreKey);
    }

    private static string BuildScoreFallbackKey(int? fixtureId, GoalEventInfo goal)
    {
        if (!fixtureId.HasValue)
            return "";

        int previousHomeGoals = goal.HomeGoals - (goal.IsHome ? 1 : 0);
        int previousAwayGoals = goal.AwayGoals - (goal.IsHome ? 0 : 1);
        return $"score|{fixtureId.Value}|{previousHomeGoals}-{previousAwayGoals}|{goal.HomeGoals}-{goal.AwayGoals}";
    }

    private static string BuildGoalMessage(
        TipsMatch tip,
        Match match,
        bool homeScored,
        MatchEvent? evt,
        int homeGoals,
        int awayGoals,
        string? playerName = null,
        bool includePlayer = true)
    {
        string symbol = Helpers.GetEventSymbol(tip, GetSymbol(homeGoals, awayGoals));
        string score = Helpers.FormatScore(homeGoals, awayGoals, homeScored);
        string detail = "";

        if (string.Equals(evt?.Detail, "Own Goal", StringComparison.OrdinalIgnoreCase))
            detail = " (Självmål)";
        else if (string.Equals(evt?.Detail, "Penalty", StringComparison.OrdinalIgnoreCase))
            detail = " (Straff)";

        string player = includePlayer && !string.IsNullOrWhiteSpace(playerName ?? evt?.Player)
            ? $" - {playerName ?? evt?.Player}{detail}"
            : "";
        string minute = evt == null ? Helpers.GetMinute(match) : Helpers.GetMinute(evt);
        return $"⚽ {symbol} Mål! {tip.HomeTeam} {score} {tip.AwayTeam} {minute}{player}";
    }

    private static string BuildGoalIdentity(TipsMatch tip, bool homeScored, int homeGoals, int awayGoals)
    {
        string score = Helpers.FormatScore(homeGoals, awayGoals, homeScored);
        return $"Mål! {tip.HomeTeam} {score} {tip.AwayTeam}";
    }

    private async Task AnnounceGoalCancelledAsync(IMessageChannel channel, TipsMatch tip, Match match, bool isHome, MatchEvent ev, string key)
    {
        string symbol = isHome
            ? Helpers.GetEventSymbol(tip, match.Symbol, match.HomeTeam, isHomeEvent: true, isBadEvent: true)
            : Helpers.GetEventSymbol(tip, match.Symbol, match.AwayTeam, isHomeEvent: false, isBadEvent: true);

        string score = Helpers.FormatScore(match.HomeGoals, match.AwayGoals, isHome);
        string message = $"⚠️ {symbol} Mål bortdömt! {tip.HomeTeam} {score} {tip.AwayTeam} {Helpers.GetMinute(ev)}";

        await _discord.AnnounceAsync(channel, message, ConsoleColor.Red, "Cancelled goal announced", couponEvent: new CouponEvent
        {
            Key = key,
            Type = "CancelledGoal",
            FixtureId = match.Id,
            Detail = ev.Detail,
            TeamId = ev.TeamId,
            Team = ev.Team ?? (isHome ? match.HomeTeam : match.AwayTeam),
            Elapsed = ev.Elapsed,
            Extra = ev.Extra,
            Score = match.Score,
            Text = message,
            PlayerId = ev.PlayerId,
            Player = ev.Player,
            CreatedUtc = DateTime.UtcNow
        });
    }

    private static bool IsScoringGoalEvent(MatchEvent ev)
    {
        if (!string.Equals(ev.Type, "Goal", StringComparison.OrdinalIgnoreCase))
            return false;

        return !string.Equals(ev.Detail, "Missed Penalty", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSymbol(int homeGoals, int awayGoals)
    {
        if (homeGoals > awayGoals) return "1";
        if (homeGoals < awayGoals) return "2";
        return "X";
    }
}
