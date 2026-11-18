namespace PlingBot.Utils;

using Microsoft.Playwright;

// Återanvänder en redan uppkopplad Chromium-instans istället för att starta om den vid
// varje sidhämtning — delad av de scraper-tjänster som renderar Svenska Spels sidor.
public class PlaywrightBrowser
{
    private IPlaywright? playwright;
    private IBrowser? browser;

    public async Task<IPage> NewPageAsync()
    {
        if (browser == null || !browser.IsConnected)
        {
            playwright?.Dispose();
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }

        return await browser.NewPageAsync();
    }
}
