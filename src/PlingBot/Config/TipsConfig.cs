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
        var s = Regex.Replace(round, @"^Regular Season - (\d+)$", "Omgång $1");
        s = Regex.Replace(s, @" - (\d+)$", " - Omgång $1");
        s = Regex.Replace(s, @"\bRound of 32\b", "Sextondelsfinal", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bQuarter\s*-?\s*Finals?\b", "Kvartsfinal", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bQuater\s*-?\s*Finals?\b", "Kvartsfinal", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bSemi\s*-?\s*Finals?\b", "Semifinal", RegexOptions.IgnoreCase);
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
    private readonly string jsonPath;

    private readonly string jsonFileName;

    private const int MaxLookbackDays = 30;
    private const int MaxLookaheadDays = 14;

    public TipsConfig(Logger logger, string game, DateOnly? couponDate = null)
    {
        _logger = logger;

        string filePrefix = GetFilePrefix(game);
        string jsonDir = ResolveJsonDirectory();
        Directory.CreateDirectory(jsonDir);

        DateOnly selectedDate = couponDate ?? ResolveCouponDate(jsonDir, filePrefix);
        jsonFileName = $"{filePrefix}_{selectedDate:yyyy-MM-dd}.json";
        _logger.Log($"Using game: {game}", ConsoleColor.Cyan);

        jsonPath = Path.Combine(jsonDir, jsonFileName);
        LoadFromJson();
    }

    // Väljer vilken kupong som ska laddas. Den senaste kupongen som redan finns på disk
    // (t.ex. en Stryktipsomgång inlämnad på lördag) kan fortfarande ha matcher kvar att
    // spela flera dagar senare (söndag, måndag). Om den gör det fortsätter vi använda den
    // istället för att av misstag hoppa till en redan utskrapad kommande omgång bara för
    // att den råkar ha lagts upp under tiden. Först när den senaste kupongen inte längre
    // har någon match kvar idag letar vi framåt efter nästa.
    private static DateOnly ResolveCouponDate(string jsonDir, string filePrefix)
    {
        DateOnly today = DateOnly.FromDateTime(SwedishTime.Now());

        DateOnly? latestExisting = FindExistingDate(jsonDir, filePrefix, today, MaxLookbackDays, forward: false);
        if (latestExisting.HasValue && CouponHasMatchOn(jsonDir, filePrefix, latestExisting.Value, today))
            return latestExisting.Value;

        DateOnly? next = FindExistingDate(jsonDir, filePrefix, today, MaxLookaheadDays, forward: true);
        if (next.HasValue)
            return next.Value;

        return latestExisting ?? today;
    }

    private static DateOnly? FindExistingDate(string jsonDir, string filePrefix, DateOnly start, int maxDays, bool forward)
    {
        for (int i = 0; i <= maxDays; i++)
        {
            DateOnly candidate = forward ? start.AddDays(i) : start.AddDays(-i);
            string candidatePath = Path.Combine(jsonDir, $"{filePrefix}_{candidate:yyyy-MM-dd}.json");
            if (File.Exists(candidatePath))
                return candidate;
        }

        return null;
    }

    // Läser bara TipsData/KickoffUtc ur filen (inte hela strukturen via LoadFromJson) för
    // att slippa sätta igång hela laddningsflödet bara för att kolla ett datum. KickoffUtc
    // sätts av ScorePollerService/FixtureMappingService redan vid första uppstarten för en
    // kupong, långt innan första avspark, så det finns tillgängligt för alla omgångar som
    // boten någonsin körts mot.
    private static bool CouponHasMatchOn(string jsonDir, string filePrefix, DateOnly couponDate, DateOnly targetDate)
    {
        string path = Path.Combine(jsonDir, $"{filePrefix}_{couponDate:yyyy-MM-dd}.json");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("TipsData", out var tipsData))
                return false;

            foreach (var tip in tipsData.EnumerateArray())
            {
                if (!tip.TryGetProperty("KickoffUtc", out var kickoffElem) ||
                    kickoffElem.ValueKind != JsonValueKind.String ||
                    !kickoffElem.TryGetDateTime(out var kickoffUtc))
                    continue;

                if (DateOnly.FromDateTime(SwedishTime.ToLocal(kickoffUtc)) == targetDate)
                    return true;
            }
        }
        catch (Exception)
        {
            // Trasig eller oläsbar fil — låt anroparen falla tillbaka på annan logik.
        }

        return false;
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
        if (File.Exists(jsonPath))
        {
            try
            {
                var json = File.ReadAllText(jsonPath, Encoding.UTF8);
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
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var json = JsonSerializer.Serialize(Data, options);
        File.WriteAllText(jsonPath, json, Encoding.UTF8);
    }

    public List<TipsMatch> TipsMatches => Data.TipsData;
}
