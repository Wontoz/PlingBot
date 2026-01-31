namespace PlingBot;
using Discord;
using Discord.WebSocket;
using DotNetEnv;
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

    public BotHost(
        ScorePollerService poller,
        MessageHandler messageHandler,
        Logger logger)
    {
        _poller = poller;
        _messageHandler = messageHandler;
        _logger = logger;

        Env.Load();

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
        });

        _client.Log += msg => { _logger.Log(msg.ToString()); return Task.CompletedTask; };
        _client.MessageReceived += _messageHandler.HandleMessageAsync;
    }

    public async Task RunAsync()
    {
        string? token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("DISCORD_TOKEN not set");

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        _logger.Log("Bot logged in and started", ConsoleColor.Green);

        // Start background polling
        _ = _poller.StartPollingAsync(_client);
    }
}