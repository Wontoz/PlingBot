namespace PlingBot.Services;

using Discord;
using System;
using System.Threading.Tasks;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class AnnouncementService
{
    private readonly FootballApiClient _api;
    private readonly TipsConfig _tipsConfig;
    private readonly Logger _logger;

    public AnnouncementService(
        FootballApiClient api,
        TipsConfig tipsConfig,
        Logger logger)
    {
        _api = api;
        _tipsConfig = tipsConfig;
        _logger = logger;
    }

    public async Task ProcessMatchUpdateAsync(IMessageChannel channel, TipsMatch tip)
    {
        var match = tip.Match ?? throw new ArgumentNullException(nameof(tip.Match));

        _logger.Log($"Checked match {tip.Number,-2}: {match.HomeTeam} - {match.AwayTeam}", ConsoleColor.DarkGray);

        // First time: just sync, no announcement
        if (!tip.LastHomeGoals.HasValue || !tip.LastAwayGoals.HasValue)
        {
            await SyncInitialScoresAsync(tip, match);
            return;
        }

        int homeDiff = match.HomeGoals - tip.LastHomeGoals.Value;
        int awayDiff = match.AwayGoals - tip.LastAwayGoals.Value;

        bool anyNewGoals = false;

        if (homeDiff > 0)
        {
            anyNewGoals = true;
            var latestGoal = await _api.FetchLatestGoalAsync(match.Id);
            for (int i = 0; i < homeDiff; i++)
            {
                await AnnounceGoalAsync(channel, tip, match, true, latestGoal);
            }
        }

        if (awayDiff > 0)
        {
            anyNewGoals = true;
            var latestGoal = await _api.FetchLatestGoalAsync(match.Id);
            for (int i = 0; i < awayDiff; i++)
            {
                await AnnounceGoalAsync(channel, tip, match, false, latestGoal);
            }
        }

        if (anyNewGoals)
        {
            tip.LastHomeGoals = match.HomeGoals;
            tip.LastAwayGoals = match.AwayGoals;
            tip.HomeScore     = match.HomeGoals;
            tip.AwayScore     = match.AwayGoals;

            _tipsConfig.SaveToJson();

            _logger.Log(
                $"Processed +{homeDiff}/{awayDiff} goals for tip {tip.Number} → stored {match.HomeGoals}-{match.AwayGoals}",
                ConsoleColor.Cyan
            );
        }
        else if (homeDiff < 0 || awayDiff < 0)
        {
            _logger.Log(
                $"Score drift (API glitch?) tip {tip.Number}: stored {tip.LastHomeGoals}-{tip.LastAwayGoals} → API {match.HomeGoals}-{match.AwayGoals} — ignoring",
                ConsoleColor.DarkRed
            );
        }
    }

    private async Task SyncInitialScoresAsync(TipsMatch tip, Match match)
    {
        tip.LastHomeGoals = match.HomeGoals;
        tip.LastAwayGoals = match.AwayGoals;
        tip.HomeScore     = match.HomeGoals;
        tip.AwayScore     = match.AwayGoals;

        _tipsConfig.SaveToJson();

        _logger.Log(
            $"Initial sync for tip {tip.Number}: {match.HomeGoals}-{match.AwayGoals}",
            ConsoleColor.DarkGray
        );
    }

    private async Task AnnounceGoalAsync(
        IMessageChannel channel,
        TipsMatch tip,
        Match match,
        bool homeTeamScored,
        MatchEvent? latestGoal)
    {
        string emoji = tip.Tip.Contains(match.Symbol) ? "✅" : "❌";

        string scoreDisplay = homeTeamScored
            ? $"**{match.HomeGoals}** - {match.AwayGoals}"
            : $"{match.HomeGoals} - **{match.AwayGoals}**";

        string minute = !string.IsNullOrWhiteSpace(latestGoal?.ExtraTime)
            ? $"{match.Elapsed} ({latestGoal.ExtraTime})'"
            : $"{match.Elapsed}'";

        string message = $"⚽ {emoji} Mål! {match.HomeTeam} {scoreDisplay} {match.AwayTeam} ({minute})";

        await channel.SendMessageAsync(message);

        _logger.Log($"Announced goal: {message}", ConsoleColor.Magenta);
    }
}