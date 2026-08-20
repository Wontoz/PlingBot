using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;

class Program
{
    private static readonly HashSet<string> AllowedPlayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fredrik", "Jonas", "William"
    };

    public static async Task Main(string[] args)
    {
        try
        {
            GameType selectedGame = GetGameFromArgs(args);

            if (args.Contains("--payouts-only"))
            {
                await UpdatePayoutsOnlyAsync(selectedGame, args);
                return;
            }

            string player = GetPlayerFromArgs(args);

            var teamRegistry = LoadTeamRegistry(args);
            var coupon = await ScrapeCouponAsync(selectedGame, teamRegistry);
            DateTime couponDate = GetCouponDate(coupon.StartTime);

            var result = new StryktipsetJson
            {
                MetaData = new MetaData
                {
                    Player = NormalizePlayerName(player),
                    Date = couponDate.ToString("yyyy-MM-dd"),
                    TotalCorrect = 0,
                    Game = selectedGame.DisplayName,
                    StartTime = coupon.StartTime,
                    DataLastUpdatedUtc = coupon.Tips.Any(t => t.Percentage1.HasValue) ? DateTime.UtcNow : null,
                },
                TipsData = coupon.Tips,
                Events = new List<CouponEventJson>()
            };

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            string json = JsonSerializer.Serialize(result, jsonOptions);

            string fileName = string.Format(
                "{0}_{1:yyyy-MM-dd}.json",
                selectedGame.FilePrefix,
                couponDate);

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

    private static Dictionary<string, (string ApiName, int? TeamId)> LoadTeamRegistry(string[] args)
    {
        try
        {
            string jsonDir = ResolvePlingBotJsonFolder(args);
            string dataFile = Path.Combine(Path.GetDirectoryName(jsonDir)!, "data", "teams.json");
            if (!File.Exists(dataFile))
                return new Dictionary<string, (string, int?)>(StringComparer.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(File.ReadAllText(dataFile, Encoding.UTF8));
            var result = new Dictionary<string, (string, int?)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                string name = entry.GetProperty("Name").GetString() ?? "";
                string api = entry.GetProperty("ApiName").GetString() ?? "";
                int? teamId = entry.TryGetProperty("Id", out var tidElem) && tidElem.ValueKind != JsonValueKind.Null
                    ? tidElem.GetInt32()
                    : null;
                result[name] = (api, teamId);
            }
            Console.WriteLine($"Team registry loaded: {result.Count} entries");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not load team registry: {ex.Message}");
            return new Dictionary<string, (string, int?)>(StringComparer.OrdinalIgnoreCase);
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

    private static async Task UpdatePayoutsOnlyAsync(GameType game, string[] args)
    {
        string date = GetArgValue(args, "--date") ?? GetStockholmNow().ToString("yyyy-MM-dd");
        string jsonDir = ResolvePlingBotJsonFolder(args);
        string fileName = $"{game.FilePrefix}_{date}.json";
        string filePath = Path.Combine(jsonDir, fileName);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"JSON file not found: {filePath}. Use --date yyyy-MM-dd to specify a date.");

        Console.WriteLine($"Updating payouts in: {filePath}");

        var payouts = await ScrapePayoutsAsync(game, date);
        if (payouts.Count == 0)
        {
            Console.WriteLine("No payout data found — page may not have results yet.");
            return;
        }

        string rawJson = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
        var node = System.Text.Json.Nodes.JsonNode.Parse(rawJson)!;

        var payoutsArray = new System.Text.Json.Nodes.JsonArray();
        foreach (var p in payouts)
        {
            var rowNode = new System.Text.Json.Nodes.JsonObject
            {
                ["Correct"] = p.Correct,
                ["Amount"]  = p.Amount,
                ["Rows"]    = p.Rows
            };
            payoutsArray.Add(rowNode);
        }
        node["MetaData"]!["Payouts"] = payoutsArray;

        var writeOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        await File.WriteAllTextAsync(filePath, node.ToJsonString(writeOptions), Encoding.UTF8);
        Console.WriteLine($"Payouts saved: {payouts.Count} row(s)");
        foreach (var p in payouts)
            Console.WriteLine($"  {p.Correct} rätt — {p.Amount} ({p.Rows})");
    }

    private static async Task<List<PayoutRowJson>> ScrapePayoutsAsync(GameType game, string date)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        string url = $"https://spela.svenskaspel.se/{game.FilePrefix}/resultat/{date}/statistik";
        Console.WriteLine($"Scraping: {url}");

        try
        {
            await page.GotoAsync(url);
            await page.WaitForSelectorAsync(".pg_windiv--result", new PageWaitForSelectorOptions { Timeout = 15000 });

            var rows = await page.EvaluateAsync<string[][]>("""
                () => Array.from(document.querySelectorAll('.pg_windiv--result li[role="row"]'))
                    .map(row => [
                        row.querySelector('.pg_windiv_grid__correct_amounts')?.textContent?.trim() ?? '',
                        row.querySelector('.pg_windiv_grid__win_commission')?.textContent?.trim() ?? '',
                        row.querySelector('.pg_windiv_grid__correct_rows')?.textContent?.trim() ?? ''
                    ])
                """);

            return rows
                .Where(r => r.Length == 3 && !string.IsNullOrEmpty(r[0]))
                .Select(r => new PayoutRowJson { Correct = r[0], Amount = r[1], Rows = r[2] })
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not scrape payouts: {ex.Message}");
            return [];
        }
    }

    private static async Task<CouponScrapeResult> ScrapeCouponAsync(
        GameType selectedGame,
        Dictionary<string, (string ApiName, int? TeamId)> teamRegistry)
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
        var percentageRows = await ScrapePercentageRowsAsync(page);
        DateTime? startTime = await ScrapeStartTimeAsync(page);

        var tips = new List<TipsMatchJson>();
        int index = 1;

        foreach (var match in matches)
        {
            var home = await match.QuerySelectorAsync(".participant.home-participant");
            var away = await match.QuerySelectorAsync(".participant.away-participant");

            string homeText = home != null ? (await home.InnerTextAsync()).Trim() : "";
            string awayText = away != null ? (await away.InnerTextAsync()).Trim() : "";

            var (homeKey, homeTeamId) = teamRegistry.TryGetValue(homeText, out var hInfo)
                ? (hInfo.ApiName, hInfo.TeamId)
                : (homeText, (int?)null);
            var (awayKey, awayTeamId) = teamRegistry.TryGetValue(awayText, out var aInfo)
                ? (aInfo.ApiName, aInfo.TeamId)
                : (awayText, (int?)null);

            var percentages = index <= percentageRows.Count
                ? percentageRows[index - 1]
                : new CouponPercentages();

            tips.Add(new TipsMatchJson
            {
                Number = index++,
                HomeTeam = homeText,
                AwayTeam = awayText,
                HomeKey = homeKey,
                AwayKey = awayKey,
                Tip = "",
                Outcome = "",
                FixtureId = null,
                HomeTeamId = homeTeamId,
                AwayTeamId = awayTeamId,
                IsFinished = false,
                HomeScore = 0,
                AwayScore = 0,
                LastHomeGoals = 0,
                LastAwayGoals = 0,
                Percentage1 = percentages.One,
                PercentageX = percentages.X,
                Percentage2 = percentages.Two,
                Odds1 = percentages.Odds1,
                OddsX = percentages.OddsX,
                Odds2 = percentages.Odds2,
                LastUpdatedUtc = null,
                LastRedCardCheckUtc = null,
                AnnouncedEventKeys = new HashSet<string>(),
            });
        }

        return new CouponScrapeResult(tips, startTime);
    }

    private static async Task<DateTime?> ScrapeStartTimeAsync(IPage page)
    {
        var closeElement = await page.QuerySelectorAsync(".pg_draw_card__reg_close_time.pg_draw_card_component");
        if (closeElement == null)
            return null;

        string closeText = await closeElement.InnerTextAsync();
        return ParseSwedishStartTimeUtc(closeText, GetStockholmNow());
    }

    private static async Task<List<CouponPercentages>> ScrapePercentageRowsAsync(IPage page)
    {
        var rawRows = await page.EvaluateAsync<string[][]>(
            """
            () => Array.from(document.querySelectorAll('[data-testid="coupon-row-tips-info-svenska-folket"]'))
                .map(row => Array.from(row.querySelectorAll('td div'))
                    .slice(0, 3)
                    .map(cell => cell.textContent.trim()))
            """);

        var rawOdds = await page.EvaluateAsync<string[][]>(
            """
            () => Array.from(document.querySelectorAll('[data-testid="coupon-row-tips-info-odds"]'))
                .map(row => Array.from(row.querySelectorAll('td div.stat-trend'))
                    .slice(0, 3)
                    .map(el => el.textContent.trim()))
            """);

        var rows = new List<CouponPercentages>();

        for (int i = 0; i < rawRows.Length; i++)
        {
            var rawRow = rawRows[i];
            var oddsRow = rawOdds.Length > i ? rawOdds[i] : [];

            rows.Add(new CouponPercentages
            {
                One = rawRow.Length > 0 ? ParsePercentage(rawRow[0]) : null,
                X = rawRow.Length > 1 ? ParsePercentage(rawRow[1]) : null,
                Two = rawRow.Length > 2 ? ParsePercentage(rawRow[2]) : null,
                Odds1 = oddsRow.Length > 0 ? ParseOdds(oddsRow[0]) : null,
                OddsX = oddsRow.Length > 1 ? ParseOdds(oddsRow[1]) : null,
                Odds2 = oddsRow.Length > 2 ? ParseOdds(oddsRow[2]) : null
            });
        }

        return rows;
    }

    private static int? ParsePercentage(string value)
    {
        value = value.Trim().TrimEnd('%').Trim();
        return int.TryParse(value, out int percentage) ? percentage : null;
    }

    private static decimal? ParseOdds(string value)
    {
        value = value.Trim();
        if (decimal.TryParse(value, NumberStyles.Any, new CultureInfo("sv-SE"), out decimal odds))
            return odds;
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal oddsEn))
            return oddsEn;
        return null;
    }

    private static DateTime GetCouponDate(DateTime? startTimeUtc)
    {
        if (!startTimeUtc.HasValue)
            return DateTime.Today;

        return TimeZoneInfo.ConvertTimeFromUtc(startTimeUtc.Value, GetStockholmTimeZone()).Date;
    }

    private static DateTime? ParseSwedishStartTimeUtc(string value, DateTime nowLocal)
    {
        var match = Regex.Match(
            value,
            @"(?:(?<day>idag|i dag|imorgon|i morgon|måndag|tisdag|onsdag|torsdag|fredag|lördag|söndag|\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2})\s+)?(?<time>\d{1,2}:\d{2})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
            return null;

        if (!TimeOnly.TryParseExact(match.Groups["time"].Value, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return null;

        string rawDay = match.Groups["day"].Value;
        DateOnly date = ResolveSwedishDate(rawDay, nowLocal);
        var localStart = date.ToDateTime(time, DateTimeKind.Unspecified);

        if (IsSwedishWeekday(rawDay) && localStart <= nowLocal)
            localStart = localStart.AddDays(7);

        return TimeZoneInfo.ConvertTimeToUtc(localStart, GetStockholmTimeZone());
    }

    private static DateOnly ResolveSwedishDate(string rawDay, DateTime nowLocal)
    {
        if (string.IsNullOrWhiteSpace(rawDay))
            return DateOnly.FromDateTime(nowLocal);

        string day = rawDay.Trim().ToLower(new CultureInfo("sv-SE"));

        if (day is "idag" or "i dag")
            return DateOnly.FromDateTime(nowLocal);

        if (day is "imorgon" or "i morgon")
            return DateOnly.FromDateTime(nowLocal.AddDays(1));

        if (DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exactDate))
            return DateOnly.FromDateTime(exactDate);

        if (DateTime.TryParseExact(day, "d/M", CultureInfo.InvariantCulture, DateTimeStyles.None, out var shortDate))
            return new DateOnly(nowLocal.Year, shortDate.Month, shortDate.Day);

        DayOfWeek? weekday = ParseSwedishWeekday(day);
        if (!weekday.HasValue)
            return DateOnly.FromDateTime(nowLocal);

        int daysForward = ((int)weekday.Value - (int)nowLocal.DayOfWeek + 7) % 7;
        return DateOnly.FromDateTime(nowLocal.AddDays(daysForward));
    }

    private static DayOfWeek? ParseSwedishWeekday(string value)
    {
        return value switch
        {
            "måndag" => DayOfWeek.Monday,
            "tisdag" => DayOfWeek.Tuesday,
            "onsdag" => DayOfWeek.Wednesday,
            "torsdag" => DayOfWeek.Thursday,
            "fredag" => DayOfWeek.Friday,
            "lördag" => DayOfWeek.Saturday,
            "söndag" => DayOfWeek.Sunday,
            _ => null
        };
    }

    private static bool IsSwedishWeekday(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string day = value.Trim().ToLower(new CultureInfo("sv-SE"));
        return ParseSwedishWeekday(day).HasValue;
    }

    private static TimeZoneInfo GetStockholmTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }

    private static DateTime GetStockholmNow()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GetStockholmTimeZone());
    }
}

public sealed record CouponScrapeResult(List<TipsMatchJson> Tips, DateTime? StartTime);

public class CouponPercentages
{
    public int? One { get; set; }
    public int? X { get; set; }
    public int? Two { get; set; }
    public decimal? Odds1 { get; set; }
    public decimal? OddsX { get; set; }
    public decimal? Odds2 { get; set; }
    public bool HasAllValues => One.HasValue && X.HasValue && Two.HasValue;
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
    public List<CouponEventJson> Events { get; set; } = new List<CouponEventJson>();
}

public class MetaData
{
    public string Player { get; set; } = "";
    public string Date { get; set; } = "";
    public int TotalCorrect { get; set; }
    public string Game { get; set; } = "";
    public DateTime? StartTime { get; set; }
    public DateTime? DataLastUpdatedUtc { get; set; }
    public List<PayoutRowJson> Payouts { get; set; } = new();
}

public class PayoutRowJson
{
    public string Correct { get; set; } = "";
    public string Amount { get; set; } = "";
    public string Rows { get; set; } = "";
}

public class TipsMatchJson
{
    public int Number { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public string HomeKey { get; set; } = "";
    public string AwayKey { get; set; } = "";
    public string Tip { get; set; } = "";
    public string Outcome { get; set; } = "";
    public int? FixtureId { get; set; }
    public int? HomeTeamId { get; set; }
    public int? AwayTeamId { get; set; }
    public bool IsFinished { get; set; }

    public int HomeScore { get; set; }
    public int AwayScore { get; set; }

    public int LastHomeGoals { get; set; }
    public int LastAwayGoals { get; set; }

    public int? Percentage1 { get; set; }
    public int? PercentageX { get; set; }
    public int? Percentage2 { get; set; }
    public decimal? Odds1 { get; set; }
    public decimal? OddsX { get; set; }
    public decimal? Odds2 { get; set; }

    public DateTime? LastUpdatedUtc { get; set; }
    public DateTime? LastRedCardCheckUtc { get; set; }

    public HashSet<string> AnnouncedEventKeys { get; set; } = new HashSet<string>();
}

public class CouponEventJson
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = "";
    public int FixtureId { get; set; }
    public string? Detail { get; set; }
    public int? TeamId { get; set; }
    public string Team { get; set; } = "";
    public int Elapsed { get; set; }
    public int Extra { get; set; }
    public string Score { get; set; } = "";
    public string Text { get; set; } = "";
    public int? PlayerId { get; set; }
    public string? Player { get; set; }
    public int? AssistId { get; set; }
    public string? Assist { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
