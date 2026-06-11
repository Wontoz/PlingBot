namespace PlingBot.Services;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
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
        EventsBuilder eventsBuilder)
    {
        _tipsConfig = tipsConfig;
        _evaluator = evaluator;
        _logger = logger;
        _dashboardService = dashboardService;
        _statusMessageService = statusMessageService;
        _syncService = syncService;
        _statsBuilder = statsBuilder;
        _eventsBuilder = eventsBuilder;

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
                var (correct, evaluated) = _evaluator.Evaluate(_tipsConfig.TipsMatches);
                string suffix = _statusMessageService.Generate(_player);

                await message.Channel.SendMessageAsync($"Just nu har vi {correct}/{evaluated} rätt!\n{suffix}");
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

}
