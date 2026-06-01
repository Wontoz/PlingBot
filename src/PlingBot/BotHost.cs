namespace PlingBot;

using Discord;
using Discord.WebSocket;
using System;
using System.Threading.Tasks;
using PlingBot.Services;
using PlingBot.Utils;

public class BotHost
{
    private readonly DiscordSocketClient _client;
    private readonly ScorePollerService _poller;
    private readonly MessageHandler _messageHandler;
    private readonly Logger _logger;
    private readonly BotOptions _options;
    private bool _pollerStarted;

    public BotHost(
        ScorePollerService poller,
        MessageHandler messageHandler,
        Logger logger,
        BotOptions options)
    {
        _poller = poller;
        _messageHandler = messageHandler;
        _logger = logger;
        _options = options;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMessages |
                GatewayIntents.MessageContent
        });

        _client.Log += msg =>
        {
            _logger.Log(msg.ToString());
            return Task.CompletedTask;
        };

        _client.MessageReceived += _messageHandler.HandleMessageAsync;
    }

    public async Task RunAsync()
    {
        string? token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("DISCORD_TOKEN not set");

        _client.Ready += OnReadyAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        _logger.Log(
            _options.TestMode ? "Bot started in TEST MODE" : "Bot logged in and started",
            ConsoleColor.Green
        );
    }

    private async Task OnReadyAsync()
    {
        if (_pollerStarted)
            return;

        _pollerStarted = true;

        _logger.Log("Discord client ready, starting poller", ConsoleColor.Green);

        _ = Task.Run(() => _poller.StartPollingAsync(_client));

        await Task.CompletedTask;
    }
    
}