namespace PlingBot.Services;

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PlingBot.Config;
using PlingBot.Utils;

public class CouponPercentageService
{
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FrequentRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FinalRefreshBeforeClose = TimeSpan.FromMinutes(5);

    private readonly TipsConfig _tipsConfig;
    private readonly BotOptions _options;
    private readonly Logger _logger;
    private DateTime? _lastRefreshUtc;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public CouponPercentageService(TipsConfig tipsConfig, BotOptions options, Logger logger)
    {
        _tipsConfig = tipsConfig;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> RefreshAsync()
    {
        try
        {
            var snapshot = await FetchCouponSnapshotAsync(GetCouponUrl(_options.Game));
            var rows = snapshot.Percentages;

            if (rows.Count == 0)
            {
                _logger.Log("No coupon percentages found", ConsoleColor.DarkYellow);
                return false;
            }

            int refreshed = 0;
            DateTime updatedUtc = DateTime.UtcNow;
            bool metadataChanged = false;
            DateTime? currentStartTime = _tipsConfig.Data.MetaData.StartTime;

            if (currentStartTime.HasValue &&
                snapshot.StartTime.HasValue &&
                currentStartTime.Value != snapshot.StartTime.Value)
            {
                _logger.Log("Coupon start time changed on source page - skipping percentage refresh", ConsoleColor.DarkYellow);
                return false;
            }

            if (snapshot.StartTime.HasValue &&
                currentStartTime != snapshot.StartTime)
            {
                _tipsConfig.Data.MetaData.StartTime = snapshot.StartTime;
                metadataChanged = true;
            }

            foreach (var tip in _tipsConfig.TipsMatches)
            {
                if (tip.Number <= 0 || tip.Number > rows.Count)
                    continue;

                var percentages = rows[tip.Number - 1];
                if (!percentages.HasAllValues)
                    continue;

                tip.Percentage1 = percentages.One;
                tip.PercentageX = percentages.X;
                tip.Percentage2 = percentages.Two;
                tip.Odds1 = percentages.Odds1;
                tip.OddsX = percentages.OddsX;
                tip.Odds2 = percentages.Odds2;
                refreshed++;
            }

            _lastRefreshUtc = updatedUtc;

            if (refreshed > 0)
                _tipsConfig.Data.MetaData.DataLastUpdatedUtc = updatedUtc;

            if (refreshed == 0 && !metadataChanged)
            {
                _logger.Log("No coupon percentages matched current tips", ConsoleColor.DarkYellow);
                return false;
            }

            _tipsConfig.SaveToJson();
            _logger.Log($"Refreshed coupon percentages for {refreshed} tips", ConsoleColor.Cyan);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to refresh coupon percentages: {ex.Message}");
            return false;
        }
    }

    public async Task RefreshIfDueAsync()
    {
        DateTime nowUtc = DateTime.UtcNow;

        if (!ShouldRefreshNow(nowUtc))
            return;

        await RefreshAsync();
    }

    private bool ShouldRefreshNow(DateTime nowUtc)
    {
        DateTime? closeUtc = _tipsConfig.Data.MetaData.StartTime;

        if (closeUtc.HasValue)
        {
            if (nowUtc >= closeUtc.Value)
                return false;

            if (IsFinalRefreshWindow(nowUtc))
                return !_lastRefreshUtc.HasValue || nowUtc - _lastRefreshUtc.Value >= FrequentRefreshInterval;
        }

        return !_lastRefreshUtc.HasValue || nowUtc - _lastRefreshUtc.Value >= DefaultRefreshInterval;
    }

    private bool IsFinalRefreshWindow(DateTime nowUtc)
    {
        DateTime? closeUtc = _tipsConfig.Data.MetaData.StartTime;

        return closeUtc.HasValue &&
            nowUtc >= closeUtc.Value - FinalRefreshBeforeClose &&
            nowUtc < closeUtc.Value;
    }

    private async Task<CouponSnapshot> FetchCouponSnapshotAsync(string url)
    {
        if (_browser == null || !_browser.IsConnected)
        {
            _playwright?.Dispose();
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }

        var page = await _browser.NewPageAsync();
        try
        {
            await page.GotoAsync(url);
            await page.WaitForSelectorAsync(
                "[data-testid=\"coupon-row-tips-info-svenska-folket\"]",
                new PageWaitForSelectorOptions { Timeout = 15000 });

            DateTime? startTime = null;
            var closeElement = await page.QuerySelectorAsync(".pg_draw_card__reg_close_time.pg_draw_card_component");
            if (closeElement != null)
            {
                string closeText = await closeElement.InnerTextAsync();
                startTime = ParseSwedishStartTimeUtc(closeText, GetStockholmNow());
            }

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

            var rows = rawRows.Select((rawRow, i) =>
            {
                var oddsRow = rawOdds.Length > i ? rawOdds[i] : [];
                return new CouponPercentages
                {
                    One  = rawRow.Length > 0 ? ParsePercentage(rawRow[0]) : null,
                    X    = rawRow.Length > 1 ? ParsePercentage(rawRow[1]) : null,
                    Two  = rawRow.Length > 2 ? ParsePercentage(rawRow[2]) : null,
                    Odds1 = oddsRow.Length > 0 ? ParseOdds(oddsRow[0]) : null,
                    OddsX = oddsRow.Length > 1 ? ParseOdds(oddsRow[1]) : null,
                    Odds2 = oddsRow.Length > 2 ? ParseOdds(oddsRow[2]) : null,
                };
            }).ToList();

            return new CouponSnapshot(rows, startTime);
        }
        finally
        {
            await page.CloseAsync();
        }
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
        var localClose = date.ToDateTime(time, DateTimeKind.Unspecified);

        if (IsSwedishWeekday(rawDay) && localClose <= nowLocal)
            localClose = localClose.AddDays(7);

        return TimeZoneInfo.ConvertTimeToUtc(localClose, GetStockholmTimeZone());
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

    private static string GetCouponUrl(string game)
    {
        if (game.Equals("Europatipset", StringComparison.OrdinalIgnoreCase))
            return "https://spela.svenskaspel.se/europatipset";

        if (game.Equals("Topptipset", StringComparison.OrdinalIgnoreCase))
            return "https://spela.svenskaspel.se/topptipset";

        return "https://spela.svenskaspel.se/stryktipset";
    }

    private sealed class CouponPercentages
    {
        public int? One { get; set; }
        public int? X { get; set; }
        public int? Two { get; set; }
        public decimal? Odds1 { get; set; }
        public decimal? OddsX { get; set; }
        public decimal? Odds2 { get; set; }
        public bool HasAllValues => One.HasValue && X.HasValue && Two.HasValue;
    }

    private sealed record CouponSnapshot(List<CouponPercentages> Percentages, DateTime? StartTime);
}
