namespace PlingBot.Services;

using Microsoft.Playwright;
using PlingBot.Config;
using PlingBot.Utils;

public class PayoutScraperService
{
    private readonly TipsConfig tipsConfig;
    private readonly Logger _logger;
    private IPlaywright? playwright;
    private IBrowser? browser;
    private CancellationTokenSource? cts;
    private readonly object syncLock = new();
    private readonly SemaphoreSlim fetchLock = new(1, 1);

    // Officiella utdelningar postas ofta en stund efter den faktiska slutsignalen (inte
    // direkt efter sista målet), så det här behöver ett generöst fönster — 20 försök
    // med en minuts mellanrum.
    private const int RetryCount = 20;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);

    public PayoutScraperService(TipsConfig tipsConfig, Logger logger)
    {
        this.tipsConfig = tipsConfig;
        _logger = logger;
    }

    public void ScheduleUpdate()
    {
        CancellationTokenSource newCts;
        lock (syncLock)
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            newCts = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < RetryCount; i++)
                {
                    await Task.Delay(RetryInterval, newCts.Token);
                    _logger.Log($"PayoutScraper: försök {i + 1}/{RetryCount}", ConsoleColor.DarkCyan);
                    if (await FetchAndUpdateAsync())
                        break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Log($"PayoutScraper: {ex.Message}", ConsoleColor.DarkYellow);
            }
        });
    }

    // Returnerar true så fort utdelningar faktiskt hittats och sparats, så att
    // retry-loopen kan avsluta tidigt istället för att alltid bränna igenom hela
    // fönstret när resultatet redan finns i hand.
    private async Task<bool> FetchAndUpdateAsync()
    {
        if (!await fetchLock.WaitAsync(0))
            return false;

        try
        {
            string game = tipsConfig.Data.MetaData.Game.ToLowerInvariant();
            string date = tipsConfig.Data.MetaData.Date;
            string url  = $"https://spela.svenskaspel.se/{game}/resultat/{date}/statistik";

            _logger.Log($"Fetching payouts: {url}", ConsoleColor.Cyan);

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

                try
                {
                    await page.WaitForSelectorAsync(
                        "li.pg_windiv_grid[role='row']",
                        new PageWaitForSelectorOptions { Timeout = 15000 });
                }
                catch (TimeoutException)
                {
                    _logger.Log("Payouts: no data found (results not yet available)", ConsoleColor.DarkYellow);
                    return false;
                }

                var rows = await page.EvaluateAsync<string[][]>(@"
                    () => Array.from(document.querySelectorAll('li.pg_windiv_grid[role=""row""]'))
                        .map(row => [
                            row.querySelector('.pg_windiv_grid__correct_amounts')?.textContent?.trim() ?? '',
                            row.querySelector('.pg_windiv_grid__win_commission')?.textContent?.replace(/ /g, ' ').trim() ?? '',
                            row.querySelector('.pg_windiv_grid__correct_rows')?.textContent?.replace(/ /g, ' ').trim() ?? '',
                        ])
                        .filter(r => r[0])
                ");

                if (rows == null || rows.Length == 0)
                {
                    _logger.Log("Payouts: no rows parsed", ConsoleColor.DarkYellow);
                    return false;
                }

                var payouts = rows.Take(4).Select(r => new PayoutRow
                {
                    Correct = r[0],
                    Amount  = r[1],
                    Rows    = r[2],
                }).ToList();

                tipsConfig.Data.MetaData.Payouts = payouts;
                tipsConfig.SaveToJson();
                _logger.Log(
                    $"Payouts saved: {string.Join(", ", payouts.Select(p => $"{p.Correct} rätt = {p.Amount}"))}",
                    ConsoleColor.Green);
                return true;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            fetchLock.Release();
        }
    }
}
