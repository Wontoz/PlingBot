namespace PlingBot.Config;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PlingBot.Models;
using PlingBot.Utils;

public class TipsDataWrapper
{
    public MetaData MetaData { get; set; } = new();
    public List<TipsMatch> TipsData { get; set; } = [];
    public List<CouponEvent> Events { get; set; } = [];
}

public class MetaData
{
    public string Player { get; set; } = string.Empty;
    public string Date { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public string Game { get; set; } = string.Empty;
    public int TotalCorrect { get; set; }
    public DateTime? StartTime { get; set; }
}

public class TipsConfig
{
    public TipsDataWrapper Data { get; private set; } = new();

    private readonly Logger _logger;
    private readonly string _jsonPath;

    private readonly string _jsonFileName;

    public TipsConfig(Logger logger, string game, DateOnly? couponDate = null)
    {
        _logger = logger;

        string filePrefix = GetFilePrefix(game);
        DateOnly selectedDate = couponDate ?? DateOnly.FromDateTime(DateTime.Today);
        _jsonFileName = $"{filePrefix}_{selectedDate:yyyy-MM-dd}.json";
        _logger.Log($"Using game: {game}", ConsoleColor.Cyan);
        _logger.Log($"Using coupon date: {selectedDate:yyyy-MM-dd}", ConsoleColor.Cyan);

        string jsonDir = ResolveJsonDirectory();
        Directory.CreateDirectory(jsonDir);

        _jsonPath = Path.Combine(jsonDir, _jsonFileName);
        LoadFromJson();
    }

    private static string GetFilePrefix(string game)
    {
        if (string.IsNullOrWhiteSpace(game))
            return "stryktipset";

        if (game.Equals("Stryktipset", StringComparison.OrdinalIgnoreCase))
            return "stryktipset";

        if (game.Equals("Europatipset", StringComparison.OrdinalIgnoreCase))
            return "europatipset";

        if (game.Equals("Topptipset", StringComparison.OrdinalIgnoreCase))
            return "topptipset";

        throw new ArgumentException("Invalid game: " + game);
    }

    private static string ResolveJsonDirectory()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, "src", "PlingBot", "json");
            string projectDir = Path.Combine(current.FullName, "src", "PlingBot");

            if (Directory.Exists(projectDir))
                return candidate;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src\\PlingBot\\json.");
    }

    private void LoadFromJson()
    {
        if (File.Exists(_jsonPath))
        {
            try
            {
                var json = File.ReadAllText(_jsonPath, Encoding.UTF8);
                Data = JsonSerializer.Deserialize<TipsDataWrapper>(json) ?? new TipsDataWrapper();

                _logger.Log(
                    $"Loaded {_jsonFileName} — {Data.TipsData.Count} tips + metadata (player: {Data.MetaData.Player}, correct: {Data.MetaData.TotalCorrect})",
                    ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load {_jsonFileName}: {ex.Message}");
                Data = new TipsDataWrapper();
            }
        }
        else
        {
            _logger.Log($"{_jsonFileName} not found — creating new empty structure", ConsoleColor.Yellow);
            Data = new TipsDataWrapper();
            SaveToJson();
        }
    }

    public void SaveToJson()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_jsonPath)!);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var json = JsonSerializer.Serialize(Data, options);
        File.WriteAllText(_jsonPath, json, Encoding.UTF8);

        _logger.Log($"Saved {_jsonFileName}", ConsoleColor.Cyan);
    }

    public List<TipsMatch> TipsMatches => Data.TipsData;
}
