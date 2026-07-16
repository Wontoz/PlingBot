namespace PlingBot.Config;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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

public class PayoutRow
{
    public string Correct { get; set; } = "";
    public string Amount { get; set; } = "";
    public string Rows { get; set; } = "";
}

public class LeagueInfo
{
    public string Name { get; set; } = "";
    public string? Flag { get; set; }
    public string? Logo { get; set; }
    public string? Round { get; set; }
    public string? RoundSwedish { get; set; }
    public string? VenueName { get; set; }

    public static string? ToSwedishRound(string? round)
    {
        if (round == null) return null;
        var s = round.Replace("Regular Season", "Grundserien");
        s = Regex.Replace(s, @" - (\d+)$", " - Omgång $1");
        return s;
    }
}

public class MetaData
{
    public string Player { get; set; } = string.Empty;
    public string Date { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public string Game { get; set; } = string.Empty;
    public int TotalCorrect { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? DataLastUpdatedUtc { get; set; }
    public List<PayoutRow> Payouts { get; set; } = [];
    public Dictionary<int, LeagueInfo> LeagueMap { get; set; } = [];
}

public class TipsConfig
{
    public TipsDataWrapper Data { get; private set; } = new();

    private readonly Logger _logger;
    private readonly string _jsonPath;

    private readonly string _jsonFileName;

    private const int MaxLookbackDays = 30;

    public TipsConfig(Logger logger, string game, DateOnly? couponDate = null)
    {
        _logger = logger;

        string filePrefix = GetFilePrefix(game);
        string jsonDir = ResolveJsonDirectory();
        Directory.CreateDirectory(jsonDir);

        DateOnly selectedDate = couponDate ?? ResolveLatestExistingDate(jsonDir, filePrefix);
        _jsonFileName = $"{filePrefix}_{selectedDate:yyyy-MM-dd}.json";
        _logger.Log($"Using game: {game}", ConsoleColor.Cyan);

        _jsonPath = Path.Combine(jsonDir, _jsonFileName);
        LoadFromJson();
    }

    // Walks backwards day-by-day from today looking for the most recent coupon JSON
    // for this game mode, so the bot doesn't spin up an empty coupon for today when
    // the latest scraped coupon is actually a few days old.
    private static DateOnly ResolveLatestExistingDate(string jsonDir, string filePrefix)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        for (int i = 0; i <= MaxLookbackDays; i++)
        {
            DateOnly candidate = today.AddDays(-i);
            string candidatePath = Path.Combine(jsonDir, $"{filePrefix}_{candidate:yyyy-MM-dd}.json");
            if (File.Exists(candidatePath))
                return candidate;
        }

        return today;
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
    }

    public List<TipsMatch> TipsMatches => Data.TipsData;
}
