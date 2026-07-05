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

        var tipsToSync = _tipsConfig.TipsMatches
            .Where(t => t.FixtureId.HasValue && !t.IsFinished && t.Match != null)
            .ToList();

        if (tipsToSync.Count > 0)
        {
            var ids = tipsToSync.Select(t => t.FixtureId!.Value).ToList();
            var batchResults = await _api.FetchCouponFixturesBatchAsync(ids);
            var resultMap = batchResults.ToDictionary(r => r.Match.Id);

            foreach (var tip in tipsToSync)
            {
                if (!resultMap.TryGetValue(tip.FixtureId!.Value, out var result))
                    continue;

                matchesChecked++;

                if (await _goals.TryHandleNewGoalEventsAsync(channel, tip, tip.Match!, result.Events))
                    eventsSynced++;

                if (await _cards.AnnounceRedCardsAsync(channel, tip, tip.Match!, result.Events))
                    eventsSynced++;
            }
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
