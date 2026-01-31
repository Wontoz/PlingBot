namespace PlingBot.Config;

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PlingBot.Utils;
using PlingBot.Models;

public class TipsConfig
{
    public List<TipsMatch> TipsMatches { get; private set; } // Mutable list for updates
    private readonly Logger _logger;
    private readonly string _jsonPath = "json/tips.json"; // Path to the JSON file (adjust if needed based on your working directory)

    public TipsConfig(Logger logger)
    {
        _logger = logger;
        LoadFromJson();
    }

    private void LoadFromJson()
    {
        if (File.Exists(_jsonPath))
        {
            try
            {
                var json = File.ReadAllText(_jsonPath);
                TipsMatches = JsonSerializer.Deserialize<List<TipsMatch>>(json) ?? new List<TipsMatch>();

                // ← Add this logging block
                _logger?.Log($"Loaded tips.json from {_jsonPath} — found {TipsMatches.Count} matches", ConsoleColor.Green);

                foreach (var m in TipsMatches.Take(5)) // show first 5 to avoid flooding console
                {
                    string fid = m.FixtureId.HasValue ? m.FixtureId.Value.ToString() : "—";
                    _logger?.Log($"  Tip #{m.Number,-2} | {m.HomeTeam,-18} vs {m.AwayTeam,-18} | fid:{fid,-8} | score:{m.HomeScore}-{m.AwayScore} | tip:{m.Tip}", ConsoleColor.DarkGray);
                }

                if (TipsMatches.Count > 5)
                    _logger?.Log($"  ... and {TipsMatches.Count - 5} more matches", ConsoleColor.DarkGray);
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to load tips.json: {ex.Message}");
                TipsMatches = new List<TipsMatch>();
            }
        }
        else
        {
            _logger?.Log($"tips.json not found at {_jsonPath} — starting with empty list", ConsoleColor.DarkRed);
            TipsMatches = new List<TipsMatch>();
            SaveToJson(); // create empty file
        }
    }

    public void TestSave()
    {
        if (TipsMatches.Count == 0)
        {
            _logger.Log("No matches to save — list is empty", ConsoleColor.Yellow);
            return;
        }

        // Make a small, visible change for testing
        var firstMatch = TipsMatches[0];
        string originalTip = firstMatch.Tip;
        firstMatch.Tip = "TEST-SAVE-" + DateTime.Now.ToString("HHmmss");

        _logger.Log($"Test save: Changed Tip #{firstMatch.Number} from '{originalTip}' → '{firstMatch.Tip}'", ConsoleColor.Yellow);

        SaveToJson();

        _logger.Log("Test save completed — check Config/tips.json", ConsoleColor.Green);
    }

    public void SaveToJson()
    {
        var json = JsonSerializer.Serialize(TipsMatches, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_jsonPath, json);
    }
    
}