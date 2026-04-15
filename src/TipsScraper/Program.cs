using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using System.Threading.Tasks;
using Microsoft.Playwright;

class Program
{
    private static readonly Dictionary<string, string> aliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // England
        {"Nottingham", "Nottingham Forest"},
        {"Wolverhampton", "Wolves"},
        {"Hull", "Hull City"},
        {"Sheffield U", "Sheffield Utd"},
        {"Sheffield W", "Sheffield Wednesday"},
        {"Stockport", "Stockport County"},
        {"Oxford", "Oxford United"},
        {"Queens Park Rangers", "QPR"},
        {"Stoke", "Stoke City"},
        {"West Bromwich", "West Brom"},
        {"Burton", "Burton Albion"},
        {"Exeter", "Exeter City"},
        {"Cambridge", "Cambridge United"},
        {"Salford", "Salford City"},
        {"Wimbledon", "AFC Wimbledon"},
        {"Mansfield", "Mansfield Town"},
        {"Accrington", "Accrington ST"},
        {"Newport", "Newport County"},
        {"Swindon", "Swindon Town"},
        {"Fleetwood", "Fleetwood Town"},


        // Sverige
        {"IFK Norrköping", "IFK Norrkoping"},
        {"Värnamo", "IFK Varnamo"},
        {"AIK", "AIK Stockholm"},
        {"Djurgården", "Djurgardens IF"},
        {"Hammarby", "Hammarby FF"},
        {"Hammarby TFF", "Hammarby Talang"},

        // Norge / Danmark
        {"Randers", "Randers FC"},
        {"Nordsjälland", "FC Nordsjaelland"},
        {"Midtjylland", "FC Midtjylland"},

        // Spanien
        {"Granada", "Granada CF"},
        {"Athletic Bilbao", "Athletic Club"},
        {"Atlético Madrid", "Atletico Madrid"},
        {"Celta de Vigo", "Celta Vigo"},

        // Tyskland
        {"Stuttgart", "VfB Stuttgart"},

        // Italien
        {"Roma", "AS Roma"},

        // Belgien
        {"Royale Union SG", "Union St. Gilloise"},

        // Portugal
        {"Porto", "FC Porto"},

        // Tjeckien
        {"Slavia Prag", "Slavia Praha"},

        // Cypern
        {"Pafos FC", "Pafos"},

        //Brasilien
        {"Botafogo RJ", "Botafogo"},

        // Landslag
        {"Sverige", "Sweden"},
        {"Schweiz", "Switzerland"},
        {"Bosnien & Herzegovina", "Bosnia & Herzegovina"},
        {"Rumänien", "Romania"},
        {"Grekland", "Greece"},
        {"Skottland", "Scotland"}
    };

    private static readonly HashSet<string> AllowedPlayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fredrik", "Jonas", "William"
    };

    public static async Task Main(string[] args)
    {
        try
        {
            string player = GetPlayerFromArgs(args);
            GameType selectedGame = GetGameFromArgs(args);

            var tips = await ScrapeMatchesAsync(selectedGame);

            var result = new StryktipsetJson
            {
                MetaData = new MetaData
                {
                    Player = NormalizePlayerName(player),
                    Date = DateTime.Today.ToString("yyyy-MM-dd"),
                    TotalCorrect = 0,
                    Game = selectedGame.DisplayName
                },
                TipsData = tips
            };

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            };

            string json = JsonSerializer.Serialize(result, jsonOptions);

            string fileName = string.Format(
                "{0}_{1:yyyy-MM-dd}.json",
                selectedGame.FilePrefix,
                DateTime.Today);

            string jsonDir = ResolvePlingBotJsonFolder(args);
            Directory.CreateDirectory(jsonDir);

            string filePath = Path.Combine(jsonDir, fileName);
            await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);

            Console.WriteLine("JSON saved to " + filePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    private static string? GetArgValue(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static string GetPlayerFromArgs(string[] args)
    {
        string? player = GetArgValue(args, "--player");

        if (string.IsNullOrWhiteSpace(player))
            throw new ArgumentException("Missing required argument: --player Fredrik|Jonas|William");

        foreach (var allowed in AllowedPlayers)
        {
            if (string.Equals(allowed, player, StringComparison.OrdinalIgnoreCase))
                return allowed;
        }

        throw new ArgumentException("Invalid --player value. Allowed: Fredrik, Jonas, William");
    }

    private static GameType GetGameFromArgs(string[] args)
    {
        string? game = GetArgValue(args, "--game");

        if (string.IsNullOrWhiteSpace(game))
            return GameType.Stryktipset;

        if (game.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            game.Equals("Stryktipset", StringComparison.OrdinalIgnoreCase))
            return GameType.Stryktipset;

        if (game.Equals("2", StringComparison.OrdinalIgnoreCase) ||
            game.Equals("Europatipset", StringComparison.OrdinalIgnoreCase))
            return GameType.Europatipset;

        if (game.Equals("3", StringComparison.OrdinalIgnoreCase) ||
            game.Equals("Topptipset", StringComparison.OrdinalIgnoreCase))
            return GameType.Topptipset;

        throw new ArgumentException("Invalid --game value. Use Stryktipset, Europatipset or Topptipset.");
    }

    private static string ResolvePlingBotJsonFolder(string[] args)
    {
        string? explicitOutputDir = GetArgValue(args, "--output-dir");
        if (!string.IsNullOrWhiteSpace(explicitOutputDir))
            return Path.GetFullPath(explicitOutputDir);

        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, "src", "PlingBot", "json");
            string plingBotProjectDir = Path.Combine(current.FullName, "src", "PlingBot");

            if (Directory.Exists(plingBotProjectDir))
                return candidate;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/PlingBot/json. Use --output-dir to specify it explicitly.");
    }

    private static string NormalizePlayerName(string input)
    {
        foreach (var allowed in AllowedPlayers)
        {
            if (string.Equals(allowed, input, StringComparison.OrdinalIgnoreCase))
                return allowed;
        }

        return input;
    }

    private static async Task<List<TipsMatchJson>> ScrapeMatchesAsync(GameType selectedGame)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var page = await browser.NewPageAsync();

        await page.GotoAsync(selectedGame.Url);
        await page.WaitForSelectorAsync(".coupon-row-description-primary");

        var matches = await page.QuerySelectorAllAsync(".coupon-row-description-primary");

        var tips = new List<TipsMatchJson>();
        int index = 1;

        foreach (var match in matches)
        {
            var home = await match.QuerySelectorAsync(".participant.home-participant");
            var away = await match.QuerySelectorAsync(".participant.away-participant");

            string homeText = home != null ? (await home.InnerTextAsync()).Trim() : "";
            string awayText = away != null ? (await away.InnerTextAsync()).Trim() : "";

            string homeKey = aliasMap.TryGetValue(homeText, out var aliasHome) ? aliasHome : homeText;
            string awayKey = aliasMap.TryGetValue(awayText, out var aliasAway) ? aliasAway : awayText;

            tips.Add(new TipsMatchJson
            {
                Number = index++,
                HomeTeam = homeText,
                AwayTeam = awayText,
                HomeKey = homeKey,
                AwayKey = awayKey,
                Tip = "",
                FixtureId = null,
                HomeScore = 0,
                AwayScore = 0,
                LastHomeGoals = 0,
                LastAwayGoals = 0,
                LastUpdatedUtc = null,
                AnnouncedEventKeys = new HashSet<string>(),
            });
        }

        return tips;
    }
}

public sealed class GameType
{
    public static readonly GameType Stryktipset = new GameType(
        "Stryktipset",
        "stryktipset",
        "https://spela.svenskaspel.se/stryktipset");

    public static readonly GameType Europatipset = new GameType(
        "Europatipset",
        "europatipset",
        "https://spela.svenskaspel.se/europatipset");

    public static readonly GameType Topptipset = new GameType(
        "Topptipset",
        "topptipset",
        "https://spela.svenskaspel.se/topptipset");

    public string DisplayName { get; private set; }
    public string FilePrefix { get; private set; }
    public string Url { get; private set; }

    private GameType(string displayName, string filePrefix, string url)
    {
        DisplayName = displayName;
        FilePrefix = filePrefix;
        Url = url;
    }
}

public class StryktipsetJson
{
    public MetaData MetaData { get; set; } = new MetaData();
    public List<TipsMatchJson> TipsData { get; set; } = new List<TipsMatchJson>();
}

public class MetaData
{
    public string Player { get; set; } = "";
    public string Date { get; set; } = "";
    public int TotalCorrect { get; set; }
    public string Game { get; set; } = "";
}

public class TipsMatchJson
{
    public int Number { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public string HomeKey { get; set; } = "";
    public string AwayKey { get; set; } = "";
    public string Tip { get; set; } = "";
    public int? FixtureId { get; set; }

    public int HomeScore { get; set; }
    public int AwayScore { get; set; }

    public int LastHomeGoals { get; set; }
    public int LastAwayGoals { get; set; }

    public DateTime? LastUpdatedUtc { get; set; }

    public HashSet<string> AnnouncedEventKeys { get; set; } = new HashSet<string>();
    public List<string> AnnouncedGoalKeys { get; set; } = new List<string>();
}