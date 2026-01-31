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
    private readonly TipsConfig _tipsConfig;   // ← Added
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

    /// <summary>
    /// Checks for new goals and announces them if detected.
    /// Updates stored scores in TipsMatch and saves to JSON when a goal occurs.
    /// </summary>
    public async Task ProcessMatchUpdateAsync(IMessageChannel channel, TipsMatch tm)
    {
        var match = tm.Match ?? throw new ArgumentNullException(nameof(tm.Match));

        _logger.Log($"✅ Checked match {tm.Number}: {match.HomeTeam} - {match.AwayTeam}");

        // First time seeing this match → sync only, no announcement
        if (tm.LastHomeGoals == null || tm.LastAwayGoals == null)
        {
            tm.LastHomeGoals = match.HomeGoals;
            tm.LastAwayGoals = match.AwayGoals;

            tm.HomeScore = match.HomeGoals;
            tm.AwayScore = match.AwayGoals;

            _tipsConfig.SaveToJson();

            _logger.Log(
                $"Initial sync for tip {tm.Number}: {match.HomeGoals}-{match.AwayGoals}",
                ConsoleColor.DarkGray
            );
            return;
        }

        int homeDiff = match.HomeGoals - tm.LastHomeGoals.Value;
        int awayDiff = match.AwayGoals - tm.LastAwayGoals.Value;
        bool anyChange = false;

        if (homeDiff > 0)
        {
            anyChange = true;
            var latest = await _api.FetchLatestGoalAsync(match.Id);
            for (int i = 0; i < homeDiff; i++)
                await AnnounceGoalAsync(channel, tm, true, latest);
        }

        if (awayDiff > 0)
        {
            anyChange = true;
            var latest = await _api.FetchLatestGoalAsync(match.Id);
            for (int i = 0; i < awayDiff; i++)
                await AnnounceGoalAsync(channel, tm, false, latest);
        }

        if (anyChange)
        {
            tm.LastHomeGoals = match.HomeGoals;
            tm.LastAwayGoals = match.AwayGoals;
            tm.HomeScore     = match.HomeGoals;
            tm.AwayScore     = match.AwayGoals;
            _tipsConfig.SaveToJson();
            _logger.Log($"Processed +{homeDiff}/{awayDiff} goals → updated stored state to {match.HomeGoals}-{match.AwayGoals}", ConsoleColor.Cyan);
        }
        else if (homeDiff < 0 || awayDiff < 0)
        {
            // Optional: log drift but do NOT update stored values downward
            _logger.Log($"Score decreased or API glitch? Tip {tm.Number}: stored {tm.LastHomeGoals}-{tm.LastAwayGoals} → API {match.HomeGoals}-{match.AwayGoals} — ignoring downward sync", ConsoleColor.DarkRed);
        }
    }

    private async Task AnnounceGoalAsync(IMessageChannel channel, TipsMatch tm, bool homeTeamScored,MatchEvent? latestGoal)
    {
        var match = tm.Match;

        string emoji = tm.Tip.Contains(match.Symbol) ? "✅" : "❌";

        string scoreDisplay = homeTeamScored
            ? $"**{match.HomeGoals}** - {match.AwayGoals}"
            : $"{match.HomeGoals} - **{match.AwayGoals}**";

        string minute = !string.IsNullOrWhiteSpace(latestGoal.ExtraTime)
            ? $"{match.Elapsed} ({latestGoal.ExtraTime})'"
            : $"{match.Elapsed}'";

        string msg = $"⚽ {emoji} Mål! {match.HomeTeam} {scoreDisplay} {match.AwayTeam} ({minute})";

        await channel.SendMessageAsync(msg);
        _logger.Log($"Announced goal: {msg}", ConsoleColor.Magenta);
    }

}