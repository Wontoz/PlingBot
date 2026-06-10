namespace PlingBot.Services;

using Discord;
using PlingBot.Config;
using PlingBot.Utils;

public class CouponEventSyncService
{
    private readonly FootballApiClient _api;
    private readonly TipsConfig _tipsConfig;
    private readonly GoalAnnouncementService _goals;
    private readonly CardAnnouncementService _cards;
    private readonly Logger _logger;

    public CouponEventSyncService(
        FootballApiClient api,
        TipsConfig tipsConfig,
        GoalAnnouncementService goals,
        CardAnnouncementService cards,
        Logger logger)
    {
        _api = api;
        _tipsConfig = tipsConfig;
        _goals = goals;
        _cards = cards;
        _logger = logger;
    }

    public async Task<(int MatchesChecked, int EventsSynced)> SyncAsync(IMessageChannel channel)
    {
        int matchesChecked = 0;
        int eventsSynced = 0;

        foreach (var tip in _tipsConfig.TipsMatches.Where(tip => tip.FixtureId.HasValue && !tip.IsFinished))
        {
            var match = tip.Match;
            if (match == null)
                continue;

            matchesChecked++;
            var matchEvents = await _api.FetchMatchEventsAsync(match.Id);

            if (await _goals.TryHandleNewGoalEventsAsync(channel, tip, match, matchEvents))
                eventsSynced++;

            if (await _cards.AnnounceRedCardsAsync(channel, tip, match, matchEvents, forceCheck: true))
                eventsSynced++;
        }

        if (eventsSynced > 0)
        {
            _tipsConfig.SaveToJson();
            _logger.Log($"Manual sync completed: {eventsSynced} event groups synced across {matchesChecked} matches", ConsoleColor.Cyan);
        }
        else
        {
            _logger.Log($"Manual sync completed: no missing events across {matchesChecked} matches", ConsoleColor.Cyan);
        }

        return (matchesChecked, eventsSynced);
    }
}
