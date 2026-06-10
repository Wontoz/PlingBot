namespace PlingBot.Services;
using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class MessageHandler
{
    private readonly TipsConfig _tipsConfig;
    private readonly CouponEvaluator _evaluator;
    private readonly Logger _logger;
    private readonly DashboardService _dashboardService;
    private readonly PlayerMessageService _statusMessageService;
    private readonly CouponEventSyncService _syncService;
    private readonly FootballApiClient _api;
    private readonly HashSet<ulong> _allowedUsers;
    private readonly ulong _williamId;
    private string _player = "";

    public MessageHandler(
        TipsConfig tipsConfig,
        CouponEvaluator evaluator,
        Logger logger,
        DashboardService dashboardService,
        PlayerMessageService statusMessageService,
        CouponEventSyncService syncService,
        FootballApiClient api)
    {
        _tipsConfig = tipsConfig;
        _evaluator = evaluator;
        _logger = logger;
        _dashboardService = dashboardService;
        _statusMessageService = statusMessageService;
        _syncService = syncService;
        _api = api;

        _williamId = ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_USER_ID_WILLIAM") ?? "0");

        _allowedUsers = new HashSet<ulong>
        {
            _williamId,
            ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_USER_ID_WIBB") ?? "0"),
            ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_USER_ID_JONAS") ?? "0")
        };

        _player = _tipsConfig.Data.MetaData.Player;
        if (!string.IsNullOrEmpty(_player)) _logger.Log("Player detected, setting player: " + _player);
    }

    public async Task HandleMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (!_allowedUsers.Contains(message.Author.Id))
        {
            await message.Channel.SendMessageAsync("He");
            return;
        }

        string content = message.Content.Trim();
        if (!content.StartsWith('!') || content.Length == 1)
            return;

        string command = content[1..].Trim().ToLowerInvariant();

        if (command.StartsWith("stats "))
        {
            await HandleStatsCommandAsync(message, command["stats ".Length..]);
            return;
        }

        switch (command)
        {
            case "status":
                var (correct, evaluated) = _evaluator.Evaluate(_tipsConfig.TipsMatches);
                string suffix = _statusMessageService.Generate(_player);

                await message.Channel.SendMessageAsync($"Just nu har vi {correct}/{evaluated} rätt!\n{suffix}");
                break;

            case "refresh":
                string extraMessage = _statusMessageService.Generate(_player);

                await _dashboardService.DeletePreviousDashboardsAsync(message.Channel);
                await _dashboardService.CreateOrUpdateAsync(message.Channel, extraMessage);
                break;

            case "updatemeta":
                if (message.Author.Id != _williamId)
                {
                    await message.Channel.SendMessageAsync("He");
                    return;
                }

                var (correctMeta, evaluatedMeta) = _evaluator.Evaluate(_tipsConfig.TipsMatches);
                _tipsConfig.Data.MetaData.TotalCorrect = correctMeta;
                _tipsConfig.SaveToJson();

                await message.Channel.SendMessageAsync(
                    $"Metadata updated: {correctMeta}/{evaluatedMeta} correct | Player: {_tipsConfig.Data.MetaData.Player} | Date: {_tipsConfig.Data.MetaData.Date}");
                break;

            case "sync":
                if (message.Author.Id != _williamId)
                {
                    await message.Channel.SendMessageAsync("He");
                    return;
                }

                var (matchesChecked, eventsSynced) = await _syncService.SyncAsync(message.Channel);
                await message.Channel.SendMessageAsync($"Sync klar: kollade {matchesChecked} matcher, synkade {eventsSynced} händelsegrupper.");
                break;
            
            case "hjälp":
            case "hjalp":
                await message.Channel.SendMessageAsync($"Följande kommandon till tillgängliga: !status !refresh !stats [matchnummer]");
            break;
        }
    }

    private async Task HandleStatsCommandAsync(SocketMessage message, string arg)
    {
        if (!int.TryParse(arg.Trim(), out int tipNumber))
        {
            await message.Channel.SendMessageAsync("Ange matchnummer, t.ex. `!stats 1`");
            return;
        }

        var tip = _tipsConfig.TipsMatches.FirstOrDefault(t => t.Number == tipNumber);
        if (tip == null)
        {
            await message.Channel.SendMessageAsync($"Hittade ingen match #{tipNumber}.");
            return;
        }

        if (!tip.FixtureId.HasValue)
        {
            await message.Channel.SendMessageAsync($"Match #{tipNumber} ({tip.HomeTeam} vs {tip.AwayTeam}) har inget fixture-ID ännu.");
            return;
        }

        var loadingMsg = await message.Channel.SendMessageAsync("Hämtar statistik...");

        var stats = await _api.FetchMatchStatisticsAsync(tip.FixtureId.Value);

        if (stats == null)
        {
            var errorMsg = await message.Channel.SendMessageAsync($"Ingen statistik tillgänglig för match #{tipNumber}");
            DeleteAfterDelay(loadingMsg, errorMsg);
            return;
        }

        var statsMsg = await message.Channel.SendMessageAsync(FormatStatistics(tipNumber, stats));
        DeleteAfterDelay(loadingMsg, statsMsg);
    }

    private static void DeleteAfterDelay(params IUserMessage[] messages)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            foreach (var msg in messages)
            {
                try { await msg.DeleteAsync(); }
                catch { }
            }
        });
    }

    private static string FormatStatistics(int tipNumber, MatchStatistics stats)
    {
        var h = stats.Home;
        var a = stats.Away;

        int colW = Math.Max(8, Math.Max(h.TeamName.Length, a.TeamName.Length));

        var sb = new StringBuilder();
        sb.AppendLine($"**#{tipNumber} {h.TeamName} vs {a.TeamName}**");
        sb.AppendLine("```");
        sb.AppendLine($"{"",18} | {h.TeamName.PadRight(colW)} | {a.TeamName.PadRight(colW)}");
        sb.AppendLine(new string('-', 18 + 3 + colW + 3 + colW));

        AppendRow(sb, "Bollinnehav", h.BallPossession, a.BallPossession, colW);
        AppendRow(sb, "Skott", h.TotalShots, a.TotalShots, colW);
        AppendRow(sb, " på mål", h.ShotsOnGoal, a.ShotsOnGoal, colW);
        AppendRow(sb, " utanför", h.ShotsOffGoal, a.ShotsOffGoal, colW);
        AppendRow(sb, " blockerade", h.BlockedShots, a.BlockedShots, colW);
        AppendRow(sb, "Hörnor", h.CornerKicks, a.CornerKicks, colW);
        AppendRow(sb, "Frisparkar", h.Fouls, a.Fouls, colW);
        AppendRow(sb, "Gula kort", h.YellowCards, a.YellowCards, colW);
        AppendRow(sb, "Röda kort", h.RedCards ?? "0", a.RedCards ?? "0", colW);
        AppendRow(sb, "Räddningar", h.GoalkeeperSaves, a.GoalkeeperSaves, colW);
        AppendRow(sb, "Passningar", FormatPasses(h), FormatPasses(a), colW);
        AppendRow(sb, " procent", h.PassesPercent, a.PassesPercent, colW);

        sb.Append("```");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string label, string? home, string? away, int colW)
    {
        sb.AppendLine($"{label,-18} | {(home ?? "-").PadRight(colW)} | {(away ?? "-").PadRight(colW)}");
    }

    private static string FormatPasses(TeamStatistics t)
    {
        if (t.PassesAccurate == null && t.TotalPasses == null) return "-";
        return $"{t.PassesAccurate ?? "?"}/{t.TotalPasses ?? "?"}";
    }
}
