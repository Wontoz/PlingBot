namespace PlingBot.Services;

using Discord;
using Discord.WebSocket;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class StatisticsBuilder
{
    private readonly TipsConfig _tipsConfig;
    private readonly FootballApiClient _api;

    public StatisticsBuilder(TipsConfig tipsConfig, FootballApiClient api)
    {
        _tipsConfig = tipsConfig;
        _api = api;
    }

    public async Task HandleAsync(SocketMessage message, string arg)
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
            Helpers.DeleteAfterDelay(TimeSpan.FromMinutes(2),loadingMsg, errorMsg);
            return;
        }

        var statsMsg = await message.Channel.SendMessageAsync(Format(tipNumber, stats));
        Helpers.DeleteAfterDelay(TimeSpan.FromMinutes(2),loadingMsg, statsMsg);
    }

    private static string Format(int tipNumber, MatchStatistics stats)
    {
        var h = stats.Home;
        var a = stats.Away;

        int colW = Math.Max(8, Math.Max(MaxContentWidth(h), MaxContentWidth(a))) + 2;

        var sb = new StringBuilder();
        sb.AppendLine($"**#{tipNumber} {h.TeamName} vs {a.TeamName}**");
        sb.AppendLine("```");
        sb.AppendLine($"{"",18} | {h.TeamName.PadRight(colW)} | {a.TeamName.PadRight(colW)}");
        sb.AppendLine(new string('-', 18 + 3 + colW + 3 + colW));

        AppendRow(sb, "Bollinnehav",  h.BallPossession,   a.BallPossession,   colW);
        AppendRow(sb, "Skott",        h.TotalShots,        a.TotalShots,        colW);
        AppendRow(sb, " på mål",      h.ShotsOnGoal,       a.ShotsOnGoal,       colW);
        AppendRow(sb, " utanför",     h.ShotsOffGoal,      a.ShotsOffGoal,      colW);
        AppendRow(sb, " blockerade",  h.BlockedShots,      a.BlockedShots,      colW);
        AppendRow(sb, "Hörnor",       h.CornerKicks,       a.CornerKicks,       colW);
        AppendRow(sb, "Frisparkar",   h.Fouls,             a.Fouls,             colW);
        AppendRow(sb, "Gula kort",    h.YellowCards,       a.YellowCards,       colW);
        AppendRow(sb, "Röda kort",    h.RedCards ?? "0",   a.RedCards ?? "0",   colW);
        AppendRow(sb, "Räddningar",   h.GoalkeeperSaves,   a.GoalkeeperSaves,   colW);
        AppendRow(sb, "Passningar",   FormatPasses(h),     FormatPasses(a),     colW);

        sb.Append("```");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string label, string? home, string? away, int colW)
    {
        sb.AppendLine($"{label,-18} | {(home ?? "-").PadRight(colW)} | {(away ?? "-").PadRight(colW)}");
    }

    private static int MaxContentWidth(TeamStatistics t)
    {
        var values = new[]
        {
            t.TeamName,
            t.BallPossession ?? "-",
            t.TotalShots     ?? "-",
            t.ShotsOnGoal    ?? "-",
            t.ShotsOffGoal   ?? "-",
            t.BlockedShots   ?? "-",
            t.CornerKicks    ?? "-",
            t.Fouls          ?? "-",
            t.YellowCards    ?? "-",
            t.RedCards       ?? "0",
            t.GoalkeeperSaves ?? "-",
            FormatPasses(t)
        };
        return values.Max(v => v.Length);
    }

    private static string FormatPasses(TeamStatistics t)
    {
        if (t.PassesAccurate == null && t.TotalPasses == null) return "-";
        return string.IsNullOrWhiteSpace(t.PassesPercent) ? $"{t.PassesAccurate ?? "?"}/{t.TotalPasses ?? "?"}" 
                                                          : $"{t.PassesAccurate ?? "?"}/{t.TotalPasses ?? "?"} ({t.PassesPercent})";
    }

}
