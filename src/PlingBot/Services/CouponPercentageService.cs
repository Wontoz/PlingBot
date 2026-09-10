namespace PlingBot.Services;

using Microsoft.Playwright;
using PlingBot.Config;
using PlingBot.Utils;

public class CouponPercentageService
{
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FrequentRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FinalRefreshBeforeClose = TimeSpan.FromMinutes(5);

    private readonly TipsConfig tipsConfig;
    private readonly BotOptions options;
    private readonly Logger _logger;
    private DateTime? lastRefreshUtc;
    private IPlaywright? playwright;
    private IBrowser? browser;

    public CouponPercentageService(TipsConfig tipsConfig, BotOptions options, Logger logger)
    {
        this.tipsConfig = tipsConfig;
        this.options = options;
        _logger = logger;
    }

    public async Task<bool> RefreshAsync()
    {
        try
        {
            var snapshot = await FetchCouponSnapshotAsync(GetCouponUrl(options.Game));
            var rows = snapshot.Percentages;

            if (rows.Count == 0)
            {
                _logger.Log("No coupon percentages found", ConsoleColor.DarkYellow);
                return false;
            }

            int refreshed = 0;
            DateTime updatedUtc = DateTime.UtcNow;
            bool metadataChanged = false;
            DateTime? currentStartTime = tipsConfig.Data.MetaData.StartTime;

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
                tipsConfig.Data.MetaData.StartTime = snapshot.StartTime;
                metadataChanged = true;
            }

            foreach (var tip in tipsConfig.TipsMatches)
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

            lastRefreshUtc = updatedUtc;

            if (refreshed > 0)
                tipsConfig.Data.MetaData.DataLastUpdatedUtc = updatedUtc;

            if (refreshed == 0 && !metadataChanged)
            {
                _logger.Log("No coupon percentages matched current tips", ConsoleColor.DarkYellow);
                return false;
            }

            tipsConfig.SaveToJson();
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
        DateTime? closeUtc = tipsConfig.Data.MetaData.StartTime;

        if (closeUtc.HasValue)
        {
            if (nowUtc >= closeUtc.Value)
                return false;

            if (IsFinalRefreshWindow(nowUtc))
                return !lastRefreshUtc.HasValue || nowUtc - lastRefreshUtc.Value >= FrequentRefreshInterval;
        }

        return !lastRefreshUtc.HasValue || nowUtc - lastRefreshUtc.Value >= DefaultRefreshInterval;
    }

    private bool IsFinalRefreshWindow(DateTime nowUtc)
    {
        DateTime? closeUtc = tipsConfig.Data.MetaData.StartTime;

        return closeUtc.HasValue &&
            nowUtc >= closeUtc.Value - FinalRefreshBeforeClose &&
            nowUtc < closeUtc.Value;
    }

    private async Task<CouponSnapshot> FetchCouponSnapshotAsync(string url)
    {
        if (browser == null || !browser.IsConnected)
        {
            playwright?.Dispose();
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }

        var page = await browser.NewPageAsync();
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
                startTime = SwedishTipsParsing.ParseSwedishStartTimeUtc(closeText, SwedishTime.Now());
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
                    One  = rawRow.Length > 0 ? SwedishTipsParsing.ParsePercentage(rawRow[0]) : null,
                    X    = rawRow.Length > 1 ? SwedishTipsParsing.ParsePercentage(rawRow[1]) : null,
                    Two  = rawRow.Length > 2 ? SwedishTipsParsing.ParsePercentage(rawRow[2]) : null,
                    Odds1 = oddsRow.Length > 0 ? SwedishTipsParsing.ParseOdds(oddsRow[0]) : null,
                    OddsX = oddsRow.Length > 1 ? SwedishTipsParsing.ParseOdds(oddsRow[1]) : null,
                    Odds2 = oddsRow.Length > 2 ? SwedishTipsParsing.ParseOdds(oddsRow[2]) : null,
                };
            }).ToList();

            return new CouponSnapshot(rows, startTime);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static string GetCouponUrl(string game)
    {
        if (game.Equals("Europatipset", StringComparison.OrdinalIgnoreCase))
            return "https://spela.svenskaspel.se/europatipset";

        if (game.Equals("Topptipset", StringComparison.OrdinalIgnoreCase))
            return "https://spela.svenskaspel.se/topptipset";

        return "https://spela.svenskaspel.se/stryktipset";
    }

    private sealed record CouponSnapshot(List<CouponPercentages> Percentages, DateTime? StartTime);
}
