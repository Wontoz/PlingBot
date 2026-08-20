namespace PlingBot.Services;
using Discord.WebSocket;
using System;
using System.Linq;
using System.Text;
using PlingBot.Config;
using PlingBot.Utils;

public class MessageHandler
{
    private readonly TipsConfig tipsConfig;
    private readonly CouponEvaluator evaluator;
    private readonly Logger _logger;
    private readonly DashboardService dashboardService;
    private readonly PlayerMessageService statusMessageService;
    private readonly CouponEventSyncService syncService;
    private readonly StatisticsBuilder statsBuilder;
    private readonly EventsBuilder eventsBuilder;
    private readonly VersusConfig versusConfig;
    private readonly BotOptions options;
    private readonly HashSet<ulong> allowedUsers;
    private readonly ulong williamId;
    private string player = "";

    public MessageHandler(
        TipsConfig tipsConfig,
        CouponEvaluator evaluator,
        Logger logger,
        DashboardService dashboardService,
        PlayerMessageService statusMessageService,
        CouponEventSyncService syncService,
        StatisticsBuilder statsBuilder,
        EventsBuilder eventsBuilder,
        VersusConfig versusConfig,
        BotOptions options)
    {
        this.tipsConfig = tipsConfig;
        this.evaluator = evaluator;
        _logger = logger;
        this.dashboardService = dashboardService;
        this.statusMessageService = statusMessageService;
        this.syncService = syncService;
        this.statsBuilder = statsBuilder;
        this.eventsBuilder = eventsBuilder;
        this.versusConfig = versusConfig;
        this.options = options;

        williamId = ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_USER_ID_WILLIAM") ?? "0");

        allowedUsers = new HashSet<ulong>
        {
            williamId,
            ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_USER_ID_WIBB") ?? "0"),
            ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_USER_ID_JONAS") ?? "0")
        };

        player = this.tipsConfig.Data.MetaData.Player;
    }

    public async Task HandleMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        var allowedChannelId = DiscordChannel.ResolveAllowedChannelId();
        if (allowedChannelId == null || message.Channel.Id != allowedChannelId.Value)
            return;

        if (!allowedUsers.Contains(message.Author.Id))
        {
            await message.Channel.SendMessageAsync("He");
            return;
        }

        string content = message.Content.Trim();
        if (!content.StartsWith('!') || content.Length == 1)
            return;

        string raw = content[1..].Trim().ToLowerInvariant();
        int space = raw.IndexOf(' ');
        string command = space >= 0 ? raw[..space] : raw;
        string arg = space >= 0 ? raw[(space + 1)..] : "";

        switch (command)
        {
            case "events":
                await eventsBuilder.HandleAsync(message, arg);
            break;

            case "procent":
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 4 ||
                    !int.TryParse(parts[0], out int matchNr) ||
                    !int.TryParse(parts[1], out int p1) ||
                    !int.TryParse(parts[2], out int pX) ||
                    !int.TryParse(parts[3], out int p2))
                {
                    await message.Channel.SendMessageAsync("Syntax: `!procent <matchnr> <1%> <X%> <2%>` — t.ex. `!procent 5 45 30 25`");
                    break;
                }

                var procentTip = tipsConfig.TipsMatches.FirstOrDefault(t => t.Number == matchNr);
                if (procentTip == null)
                {
                    await message.Channel.SendMessageAsync($"Hittade ingen match #{matchNr}.");
                    break;
                }

                procentTip.Percentage1 = p1;
                procentTip.PercentageX = pX;
                procentTip.Percentage2 = p2;
                tipsConfig.Data.MetaData.DataLastUpdatedUtc = DateTime.UtcNow;
                tipsConfig.SaveToJson();

                //await message.Channel.SendMessageAsync($"Uppdaterade procent för match #{matchNr} ({procentTip.HomeTeam} - {procentTip.AwayTeam}): 1={p1}% X={pX}% 2={p2}%");
            break;

            case "hjälp":
            case "hjalp":
                await message.Channel.SendMessageAsync("Följande kommandon är tillgängliga: !status !refresh !stats [matchnummer] !events [matchnummer]");
            break;

            case "refresh":
                string extraMessage = statusMessageService.Generate(player);

                await dashboardService.DeletePreviousDashboardsAsync(message.Channel);
                await dashboardService.CreateOrUpdateAsync(message.Channel, extraMessage);
            break;

            case "stats":
                await statsBuilder.HandleAsync(message, arg);
            break;

            case "h2h":
                if (!int.TryParse(arg.Trim(), out int h2hNr))
                {
                    await message.Channel.SendMessageAsync("Ange matchnummer, t.ex. `!h2h 3`");
                    break;
                }
                var h2hTip = tipsConfig.TipsMatches.FirstOrDefault(t => t.Number == h2hNr);
                if (h2hTip == null)
                {
                    await message.Channel.SendMessageAsync($"Hittade ingen match #{h2hNr}.");
                    break;
                }
                if (h2hTip.H2H == null || h2hTip.H2H.Count == 0)
                {
                    await message.Channel.SendMessageAsync($"Ingen H2H-data tillgänglig för match #{h2hNr} ({h2hTip.HomeTeam} vs {h2hTip.AwayTeam}).");
                    break;
                }
                var h2hMsg = await message.Channel.SendMessageAsync(FormatH2H(h2hNr, h2hTip));
                Helpers.DeleteAfterDelay(TimeSpan.FromMinutes(2), h2hMsg);
            break;

            case "status":
                if (options.IsVersusMode)
                {
                    await message.Channel.SendMessageAsync(BuildVersusScoreLine());
                    break;
                }
                else
                {
                    var (correct, evaluated) = evaluator.Evaluate(tipsConfig.TipsMatches);
                    string suffix = statusMessageService.Generate(player);

                    string statusMsg = $"Just nu har vi {correct}/{evaluated} rätt!";
                    statusMsg += $"\n{suffix}";
                    await message.Channel.SendMessageAsync(statusMsg);
                    break;
                }     
            
            case "sync":
                if (await DenyIfNotWilliamAsync(message))
                    return;

                var (matchesChecked, eventsSynced) = await syncService.SyncAsync(message.Channel);
                await message.Channel.SendMessageAsync($"Sync klar: kollade {matchesChecked} matcher, synkade {eventsSynced} händelsegrupper.");
            break;

            case "updatemeta":
                if (await DenyIfNotWilliamAsync(message))
                    return;

                var (correctMeta, evaluatedMeta) = evaluator.Evaluate(tipsConfig.TipsMatches);
                tipsConfig.Data.MetaData.TotalCorrect = correctMeta;
                tipsConfig.SaveToJson();

                await message.Channel.SendMessageAsync(
                    $"Metadata updated: {correctMeta}/{evaluatedMeta} correct | Player: {tipsConfig.Data.MetaData.Player} | Date: {tipsConfig.Data.MetaData.Date}");
            break;
        }
    }

    private async Task<bool> DenyIfNotWilliamAsync(SocketMessage message)
    {
        if (message.Author.Id == williamId)
            return false;

        await message.Channel.SendMessageAsync("He");
        return true;
    }

    private static string FormatH2H(int tipNumber, PlingBot.Models.TipsMatch tip)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**#{tipNumber} {tip.HomeTeam} vs {tip.AwayTeam} — H2H (5 senaste)**");
        sb.AppendLine("```");

        int nameW = tip.H2H!
            .SelectMany(m => new[] { m.HomeTeam, m.AwayTeam })
            .Max(s => s.Length);

        foreach (var m in tip.H2H!.OrderByDescending(m => m.Date))
        {
            string date  = m.Date.ToString("yyyy-MM-dd");
            string score = $"{m.HomeGoals}-{m.AwayGoals}";
            string home  = m.HomeTeam.PadRight(nameW);
            string away  = m.AwayTeam;
            sb.AppendLine($"{date}  {home}  {score,5}  {away}");
        }

        sb.Append("```");
        return sb.ToString();
    }

    private string BuildVersusScoreLine()
    {
        if (!options.IsVersusMode || versusConfig.Players.Count == 0)
            return "";

        var tips = tipsConfig.TipsMatches;
        var parts = new List<string>();

        string primaryName = tipsConfig.Data.MetaData.Player;
        int primaryCorrect = tips.Count(t =>
        {
            string? sym = CouponEvaluator.GetCurrentSymbol(t);
            return sym != null && !string.IsNullOrWhiteSpace(t.Tip) && t.Tip.Contains(sym);
        });
        parts.Add($"{primaryName}: {primaryCorrect}");

        foreach (var p in versusConfig.Players)
        {
            int playerCorrect = tips.Count(t =>
            {
                string? sym = CouponEvaluator.GetCurrentSymbol(t);
                string? playerTip = p.GetTip(t.Number);
                return sym != null && !string.IsNullOrWhiteSpace(playerTip) && playerTip.Contains(sym);
            });
            parts.Add($"{p.Name}: {playerCorrect}");
        }

        return "Antal rätt: " + string.Join(" | ", parts);
    }
}
