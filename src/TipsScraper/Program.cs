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
        {"Harrogate", "Harrogate Town"},


        // Sverige
        {"IFK Norrköping", "IFK Norrkoping"},
        {"Värnamo", "IFK Varnamo"},
        {"AIK", "AIK Stockholm"},
        {"Djurgården", "Djurgardens IF"},
        {"Hammarby", "Hammarby FF"},
        {"Hammarby TFF", "Hammarby Talang"},
        {"Mjällby", "Mjallby AIF"},
        {"Häcken", "BK Hacken"},
        {"Västerås", "Vasteras SK FK"},
        {"Norrby", "Norrby IF"},
        {"Elfsborg", "IF Elfsborg"},
        {"IFK Göteborg", "IFK Goteborg"},
        {"Öster", "Osters IF"},
        {"Åtvidaberg", "Atvidabergs FF"},
        {"AFC Malmö", "AFC Malmo"},
        {"Jönköpings Södra", "Jonkopings Sodra"},
        {"Gefle", "Gefle IF"},
        {"Sölvesborgs GoIF", "Sölvesborg"},
        {"Torn", "Torns"},


        // Norge / Danmark
        {"Randers", "Randers FC"},
        {"Nordsjälland", "FC Nordsjaelland"},
        {"Midtjylland", "FC Midtjylland"},
        {"Bodö/Glimt", "Bodo/Glimt"},
        {"Fredrikstad FK", "Fredrikstad"},

        // Finland
        {"IF Gnistan", "Gnistan"},
        {"Inter Åbo", "Inter Turku"},
        {"TPS", "Turku PS"},

        // Spanien
        {"Granada", "Granada CF"},
        {"Athletic Bilbao", "Athletic Club"},
        {"Atlético Madrid", "Atletico Madrid"},
        {"Celta de Vigo", "Celta Vigo"},

        // Tyskland
        {"Stuttgart", "VfB Stuttgart"},
        {"Wolfsburg", "VfL Wolfsburg"},
        {"Paderborn", "SC Paderborn 07"},

        // Italien
        {"Roma", "AS Roma"},

        // Belgien
        {"Royale Union SG", "Union St. Gilloise"},
        {"Mechelen", "KV Mechelen"},
        {"Club Brügge", "Club Brugge KV"},
        {"St. Truidense","St. Truiden"},

        // Skottland
        {"Partick Thistle", "Partick"},
        {"St. Mirren"," ST Mirren"},

        // Portugal
        {"Porto", "FC Porto"},

        // Tjeckien
        {"Slavia Prag", "Slavia Praha"},

        // Cypern
        {"Pafos FC", "Pafos"},

        //Brasilien
        {"Botafogo RJ", "Botafogo"},
        {"Paranaense", "Atletico Paranaense"},

        // Landslag
        {"Sverige", "Sweden"},
        {"Spanien", "Spain"},
        {"Irak", "Iraq"},
        {"Frankrike", "France"},
        {"Elfenbenskusten", "Ivory Coast"},
        {"Mexiko", "Mexico"},
        {"Paraguay", "Paraguay"},
        {"Brasilien", "Brazil"},
        {"Marocko", "Morocco"},
        {"Australien", "Australia"},
        {"Japan", "Japan"},
        {"Ecuador", "Ecuador"},
        {"Belgien", "Belgium"},
        {"Egypten", "Egypt"},
        {"Senegal", "Senegal"},
        {"Argentina", "Argentina"},
        {"Serbien", "Serbia"},
        {"Nederländerna", "Netherlands"},
        {"Algeriet", "Algeria"},
        {"DR Kongo", "Congo DR"},
        {"Danmark", "Denmark"},
        {"Polen", "Poland"},
        {"Nigeria", "Nigeria"},
        {"Luxemburg", "Luxembourg"},
        {"Italien", "Italy"},
        {"Albanien", "Albania"},
        {"Israel", "Israel"},
        {"Schweiz", "Switzerland"},
        {"Bosnien & Hercegovina", "Bosnia & Herzegovina"},
        {"Rumänien", "Romania"},
        {"Grekland", "Greece"},
        {"Skottland", "Scotland"},
        {"Norge", "Norway"},
        {"Turkiet", "Türkiye"},
        {"Nordmakedonien", "North Macedonia"},
        {"Österrike", "Austria"},
        {"Tunisien", "Tunisia"},
        {"Kanada", "Canada"},
        {"Sydkorea", "South Korea"},
        {"El Salvador", "El Salvador"},
        {"Sydafrika", "South Africa"},
        {"Tjeckien", "Czech Republic"},


        // Damlag (Ändra manuellt)
        {"Sverige D", "Sweden W"},
        {"Italien D", "Italy W"},
        {"Serbien D", "Serbia W"},
        {"Danmark D", "Denmark W"}
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

            var coupon = await ScrapeCouponAsync(selectedGame);
            DateTime couponDate = GetCouponDate(coupon.StartTime);

            var result = new StryktipsetJson
            {
                MetaData = new MetaData
                {
                    Player = NormalizePlayerName(player),
                    Date = couponDate.ToString("yyyy-MM-dd"),
                    TotalCorrect = 0,
                    Game = selectedGame.DisplayName,
                    StartTime = coupon.StartTime
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

    private static async Task<CouponScrapeResult> ScrapeCouponAsync(GameType selectedGame)
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

            string homeKey = aliasMap.TryGetValue(homeText, out var aliasHome) ? aliasHome : homeText;
            string awayKey = aliasMap.TryGetValue(awayText, out var aliasAway) ? aliasAway : awayText;
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
                IsFinished = false,
                HomeScore = 0,
                AwayScore = 0,
                LastHomeGoals = 0,
                LastAwayGoals = 0,
                Percentage1 = percentages.One,
                PercentageX = percentages.X,
                Percentage2 = percentages.Two,
                PercentagesUpdatedUtc = percentages.HasAllValues ? DateTime.UtcNow : null,
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

        var rows = new List<CouponPercentages>();

        foreach (var rawRow in rawRows)
        {
            rows.Add(new CouponPercentages
            {
                One = rawRow.Length > 0 ? ParsePercentage(rawRow[0]) : null,
                X = rawRow.Length > 1 ? ParsePercentage(rawRow[1]) : null,
                Two = rawRow.Length > 2 ? ParsePercentage(rawRow[2]) : null
            });
        }

        return rows;
    }

    private static int? ParsePercentage(string value)
    {
        value = value.Trim().TrimEnd('%').Trim();
        return int.TryParse(value, out int percentage) ? percentage : null;
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
    public bool IsFinished { get; set; }

    public int HomeScore { get; set; }
    public int AwayScore { get; set; }

    public int LastHomeGoals { get; set; }
    public int LastAwayGoals { get; set; }

    public int? Percentage1 { get; set; }
    public int? PercentageX { get; set; }
    public int? Percentage2 { get; set; }
    public DateTime? PercentagesUpdatedUtc { get; set; }

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
