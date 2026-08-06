namespace PlingBot;

using Discord;
using Discord.WebSocket;
using System;
using System.Threading.Tasks;
using PlingBot.Services;
using PlingBot.Utils;

public class BotHost
{
    private readonly DiscordSocketClient client;
    private readonly ScorePollerService poller;
    private readonly MessageHandler messageHandler;
    private readonly Logger _logger;
    private readonly BotOptions options;
    private bool pollerStarted;

    public BotHost(
        ScorePollerService poller,
        MessageHandler messageHandler,
        Logger logger,
        BotOptions options)
    {
        this.poller = poller;
        this.messageHandler = messageHandler;
        _logger = logger;
        this.options = options;

        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMessages |
                GatewayIntents.MessageContent
        });

        client.Log += msg =>
        {
            _logger.Log(msg.ToString());
            return Task.CompletedTask;
        };

        client.MessageReceived += this.messageHandler.HandleMessageAsync;
    }

    public async Task RunAsync()
    {
        string? token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("DISCORD_TOKEN not set");

        client.Ready += OnReadyAsync;

        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();

        _logger.Log(
            options.TestMode ? "Bot started in TEST MODE" : "Bot logged in and started",
            ConsoleColor.Green
        );
    }

    private async Task OnReadyAsync()
    {
        if (pollerStarted)
            return;

        pollerStarted = true;

        _logger.Log("Discord client ready, starting poller", ConsoleColor.Green);

        _ = Task.Run(() => poller.StartPollingAsync(client));

        await Task.CompletedTask;
    }

}