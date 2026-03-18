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
    private string _player = "";
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

        _player = _tipsConfig.Data.MetaData.Player;
        if(!string.IsNullOrEmpty(_player)) _logger.Log("Player detected, setting player: " + _player);
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

            string suffix = _player.ToLower() switch
            {
                "jonas" => _isFirstMessage ? "Eeeeeeeeeeeeeek" : "Eeeeek",
                "fredrik" => _isFirstMessage ? "PleeeeeEEEEASE Wibb" : "Körvi Wibb",
                "william" => _isFirstMessage ? "Kan ju knappast bli sämre än förra gången du körde William" : "Suck...",
                _ => ""
            };
            
            await message.Channel.SendMessageAsync($"Just nu har vi {correct}/{evaluated} rätt! {suffix}");
            _isFirstMessage = false;
        }

        if (content.Equals("!updatemeta", StringComparison.OrdinalIgnoreCase) && _allowedUsers.Contains(message.Author.Id))
        {
            var (correct, evaluated) = _evaluator.Evaluate(_tipsConfig.TipsMatches);
            _tipsConfig.Data.MetaData.TotalCorrect = correct;

            // Optional: update date/player if you want commands for that
            // _tipsConfig.Data.MetaData.RoundDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
            // _tipsConfig.Data.MetaData.Player = "william";

            _tipsConfig.SaveToJson();

            await message.Channel.SendMessageAsync(
                $"Metadata updated: {correct}/{evaluated} correct | Player: {_tipsConfig.Data.MetaData.Player} | Date: {_tipsConfig.Data.MetaData.Date}"
            );
        }

        // Add more commands here later...
    }
}