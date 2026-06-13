namespace PlingBot.Services;
using Discord.WebSocket;
using System;
using System.Linq;
using PlingBot.Config;
using PlingBot.Utils;

public class MessageHandler
{
    private readonly TipsConfig _tipsConfig;
    private readonly CouponEvaluator _evaluator;
    private readonly Logger _logger;
    private readonly DashboardService _dashboardService;
    private readonly PlayerMessageService _statusMessageService;
    private readonly CouponEventSyncService _syncService;
    private readonly StatisticsBuilder _statsBuilder;
    private readonly EventsBuilder _eventsBuilder;
    private readonly VersusConfig _versusConfig;
    private readonly BotOptions _options;
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
        StatisticsBuilder statsBuilder,
        EventsBuilder eventsBuilder,
        VersusConfig versusConfig,
        BotOptions options)
    {
        _tipsConfig = tipsConfig;
        _evaluator = evaluator;
        _logger = logger;
        _dashboardService = dashboardService;
        _statusMessageService = statusMessageService;
        _syncService = syncService;
        _statsBuilder = statsBuilder;
        _eventsBuilder = eventsBuilder;
        _versusConfig = versusConfig;
        _options = options;

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

        string raw = content[1..].Trim().ToLowerInvariant();
        int space = raw.IndexOf(' ');
        string command = space >= 0 ? raw[..space] : raw;
        string arg = space >= 0 ? raw[(space + 1)..] : "";

        switch (command)
        {
            case "events":
                await _eventsBuilder.HandleAsync(message, arg);
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

                var procentTip = _tipsConfig.TipsMatches.FirstOrDefault(t => t.Number == matchNr);
                if (procentTip == null)
                {
                    await message.Channel.SendMessageAsync($"Hittade ingen match #{matchNr}.");
                    break;
                }

                procentTip.Percentage1 = p1;
                procentTip.PercentageX = pX;
                procentTip.Percentage2 = p2;
                procentTip.PercentagesUpdatedUtc = DateTime.UtcNow;
                _tipsConfig.SaveToJson();

                //await message.Channel.SendMessageAsync($"Uppdaterade procent för match #{matchNr} ({procentTip.HomeTeam} - {procentTip.AwayTeam}): 1={p1}% X={pX}% 2={p2}%");
            break;

            case "hjälp":
            case "hjalp":
                await message.Channel.SendMessageAsync("Följande kommandon är tillgängliga: !status !refresh !stats [matchnummer] !events [matchnummer]");
            break;

            case "refresh":
                string extraMessage = _statusMessageService.Generate(_player);

                await _dashboardService.DeletePreviousDashboardsAsync(message.Channel);
                await _dashboardService.CreateOrUpdateAsync(message.Channel, extraMessage);
            break;

            case "stats":
                await _statsBuilder.HandleAsync(message, arg);
            break;

            case "status":
                if (_options.IsVersusMode)
                {
                    await message.Channel.SendMessageAsync(BuildVersusScoreLine());
                    break;
                }
                else
                {
                    var (correct, evaluated) = _evaluator.Evaluate(_tipsConfig.TipsMatches);
                    string suffix = _statusMessageService.Generate(_player);

                    string statusMsg = $"Just nu har vi {correct}/{evaluated} rätt!";
                    statusMsg += $"\n{suffix}";
                    await message.Channel.SendMessageAsync(statusMsg);
                    break;
                }     
            
            case "sync":
                if (message.Author.Id != _williamId)
                {
                    await message.Channel.SendMessageAsync("He");
                    return;
                }

                var (matchesChecked, eventsSynced) = await _syncService.SyncAsync(message.Channel);
                await message.Channel.SendMessageAsync($"Sync klar: kollade {matchesChecked} matcher, synkade {eventsSynced} händelsegrupper.");
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
        }
    }

    private string BuildVersusScoreLine()
    {
        if (!_options.IsVersusMode || _versusConfig.Players.Count == 0)
            return "";

        var tips = _tipsConfig.TipsMatches;
        var parts = new List<string>();

        string primaryName = _tipsConfig.Data.MetaData.Player;
        int primaryCorrect = tips.Count(t =>
        {
            string? sym = CouponEvaluator.GetCurrentSymbol(t);
            return sym != null && !string.IsNullOrWhiteSpace(t.Tip) && t.Tip.Contains(sym);
        });
        parts.Add($"{primaryName}: {primaryCorrect}");

        foreach (var p in _versusConfig.Players)
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
