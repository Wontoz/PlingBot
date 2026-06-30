namespace PlingBot.Services;

using Microsoft.Playwright;
using PlingBot.Config;
using PlingBot.Utils;

public class PayoutScraperService
{
    private readonly TipsConfig _tipsConfig;
    private readonly Logger _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    // Official payouts are often posted a while after the actual final whistle (not right
    // after the last goal), so this needs a generous window — 20 attempts a minute apart.
    private const int RetryCount = 20;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);

    public PayoutScraperService(TipsConfig tipsConfig, Logger logger)
    {
        _tipsConfig = tipsConfig;
        _logger = logger;
    }

    public void ScheduleUpdate()
    {
        CancellationTokenSource newCts;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            newCts = _cts;
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

    // Returns true once payouts were actually found and saved, so the retry loop can stop early
    // instead of always burning through the full window once results are already in hand.
    private async Task<bool> FetchAndUpdateAsync()
    {
        if (!await _fetchLock.WaitAsync(0))
            return false;

        try
        {
            string game = _tipsConfig.Data.MetaData.Game.ToLowerInvariant();
            string date = _tipsConfig.Data.MetaData.Date;
            string url  = $"https://spela.svenskaspel.se/{game}/resultat/{date}/statistik";

            _logger.Log($"Fetching payouts: {url}", ConsoleColor.Cyan);

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

                _tipsConfig.Data.MetaData.Payouts = payouts;
                _tipsConfig.SaveToJson();
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
            _fetchLock.Release();
        }
    }
}
