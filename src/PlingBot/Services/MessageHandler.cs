namespace PlingBot.Services;
using Discord.WebSocket;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using PlingBot.Config;
using PlingBot.Utils;

public class MessageHandler
{
    private readonly TipsConfig _tipsConfig;
    private readonly CouponEvaluator _evaluator;
    private readonly Logger _logger;
    private readonly HashSet<ulong> _allowedUsers;
    private readonly ulong _viktorId;
    private string _currentPlayer = "";
    private bool _isFirstMessage = true;

    public MessageHandler(TipsConfig tipsConfig, CouponEvaluator evaluator, Logger logger)
    {
        _tipsConfig = tipsConfig;
        _evaluator = evaluator;
        _logger = logger;

        // Load allowed users and IDs from env (you can keep hard-coding or move to env)
        _allowedUsers = new HashSet<ulong>
        {
            ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_USER_ID_WILLIAM") ?? "0"),
            ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_USER_ID_WIBB") ?? "0"),
            ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_USER_ID_JONAS") ?? "0")
        };

        _viktorId = ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_USER_ID_VIKTOR") ?? "0");

        // Player selection – could be moved to startup or command later
        while (string.IsNullOrWhiteSpace(_currentPlayer) || !new[] { "Wibb", "Ek", "Wille" }.Contains(_currentPlayer))
        {
            Console.WriteLine("Who is playing? Wibb, Ek or Wille");
            _currentPlayer = Console.ReadLine()?.Trim() ?? "";
            if (!new[] { "Wibb", "Ek", "Wille" }.Contains(_currentPlayer))
                Console.WriteLine("Invalid player. Try again.");
            else
                Console.WriteLine($"Player set to: {_currentPlayer}");
        }
    }

    public async Task HandleMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        var content = message.Content.Trim();

        if (content.Equals("!status", StringComparison.OrdinalIgnoreCase))
        {
            if (!_allowedUsers.Contains(message.Author.Id))
            {
                await message.Channel.SendMessageAsync("I är icke med på stryket, i har inget att se här.");
                if (message.Author.Id == _viktorId)
                {
                    await Task.Delay(5000);
                    await message.Channel.SendMessageAsync("Särkilt inte du Viktor.");
                }
                return;
            }

            var (correct, evaluated) = _evaluator.Evaluate(_tipsConfig.TipsMatches);

            string suffix = _currentPlayer switch
            {
                "Ek" => _isFirstMessage ? "Eeeeeeeeeeeeeek" : "Eeeeek",
                "Wibb" => _isFirstMessage ? "PleeeeeEEEEASE Wibb" : "Körvi Wibb",
                "Wille" => _isFirstMessage ? "Kan ju knappast bli sämre än förra gången du körde" : "Suck...",
                _ => ""
            };

            await message.Channel.SendMessageAsync($"Just nu har vi {correct}/{evaluated} rätt! {suffix}");
            _isFirstMessage = false;
        }

        // Add more commands here later...
    }
}