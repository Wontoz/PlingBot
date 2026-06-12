namespace PlingBot.Config;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using PlingBot.Utils;

public class VersusPlayersData
{
    public List<VersusPlayer> Players { get; set; } = [];
}

public class VersusPlayer
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<int, string> Tips { get; set; } = new();

    public string? GetTip(int matchNumber) =>
        Tips.TryGetValue(matchNumber, out var tip) ? tip : null;
}

public class VersusConfig
{
    public VersusPlayersData Data { get; private set; } = new();

    private readonly string _jsonFileName;

    public VersusConfig(Logger logger, BotOptions options)
    {
        string filePrefix = GetFilePrefix(options.Game);
        DateOnly date = options.CouponDate ?? DateOnly.FromDateTime(DateTime.Today);
        _jsonFileName = $"{filePrefix}_{date:yyyy-MM-dd}_versus.json";

        string jsonDir = ResolveJsonDirectory();
        string path = Path.Combine(jsonDir, _jsonFileName);

        if (!File.Exists(path))
        {
            logger.Log($"{_jsonFileName} not found — no versus players loaded", ConsoleColor.Yellow);
            return;
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            Data = JsonSerializer.Deserialize<VersusPlayersData>(json) ?? new VersusPlayersData();
            logger.Log($"Loaded {_jsonFileName} — {Data.Players.Count} versus players", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            logger.Log($"Failed to load {_jsonFileName}: {ex.Message}", ConsoleColor.Red);
        }
    }

    public IReadOnlyList<VersusPlayer> Players => Data.Players;

    private static string GetFilePrefix(string game)
    {
        if (string.IsNullOrWhiteSpace(game)) return "stryktipset";
        if (game.Equals("Stryktipset", StringComparison.OrdinalIgnoreCase)) return "stryktipset";
        if (game.Equals("Europatipset", StringComparison.OrdinalIgnoreCase)) return "europatipset";
        if (game.Equals("Topptipset", StringComparison.OrdinalIgnoreCase)) return "topptipset";
        throw new ArgumentException("Invalid game: " + game);
    }

    private static string ResolveJsonDirectory()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            string projectDir = Path.Combine(current.FullName, "src", "PlingBot");
            if (Directory.Exists(projectDir))
                return Path.Combine(current.FullName, "src", "PlingBot", "json");
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src\\PlingBot\\json.");
    }
}
