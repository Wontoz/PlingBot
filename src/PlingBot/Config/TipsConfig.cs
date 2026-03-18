namespace PlingBot.Config;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using PlingBot.Models;
using PlingBot.Utils;

public class TipsDataWrapper
{
    public MetaData MetaData { get; set; } = new();
    public List<TipsMatch> TipsData { get; set; } = [];
}

public class MetaData
{
    public string Player { get; set; } = string.Empty;
    public string Date { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd");
    public int TotalCorrect { get; set; }
}

public class TipsConfig
{
    public TipsDataWrapper Data { get; private set; } = new();

    private readonly Logger _logger;
    private readonly string _jsonPath;

    private readonly string jsonFileName = $"stryktipset_{DateTime.UtcNow:yyyy-MM-dd}.json";

    public TipsConfig(Logger logger)
    {
        _logger = logger;
        _jsonPath = Path.Combine("..", "PlingBot", "json", jsonFileName);
        LoadFromJson();
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
                    $"Loaded {jsonFileName} — {Data.TipsData.Count} tips + metadata (player: {Data.MetaData.Player}, correct: {Data.MetaData.TotalCorrect})",
                    ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load {jsonFileName}: {ex.Message}");
                Data = new TipsDataWrapper();
            }
        }
        else
        {
            _logger.Log($"{jsonFileName} not found — creating new empty structure", ConsoleColor.Yellow);
            Data = new TipsDataWrapper();
            SaveToJson();
        }
    }

    public void SaveToJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        var json = JsonSerializer.Serialize(Data, options);
        File.WriteAllText(_jsonPath, json, Encoding.UTF8);

        _logger.Log($"Saved {jsonFileName}", ConsoleColor.Cyan);
    }

    public List<TipsMatch> TipsMatches => Data.TipsData;
}