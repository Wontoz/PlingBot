namespace PlingBot;
using PlingBot.Models;
using PlingBot.Services;
using PlingBot.Config;
using PlingBot.Utils;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        // Attempt to locate and load .env from likely locations so constructors can read env vars
        string? envFile = null;

        // 1) Current working directory
        var cwdCandidate = Path.Combine(Environment.CurrentDirectory, ".env");
        if (File.Exists(cwdCandidate)) envFile = cwdCandidate;

        // 2) Walk up from AppContext.BaseDirectory
        if (envFile == null) envFile = FindEnvFile(AppContext.BaseDirectory, ".env");

        // 3) Look specifically for a parent folder named "PlingBot" (project root)
        if (envFile == null)
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    if (dir.Name.Equals("PlingBot", StringComparison.OrdinalIgnoreCase))
                    {
                        var candidate = Path.Combine(dir.FullName, ".env");
                        if (File.Exists(candidate))
                        {
                            envFile = candidate;
                            break;
                        }
                    }
                    dir = dir.Parent;
                }
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(envFile))
        {
            Console.WriteLine($"Loading env from: {envFile}");
            DotNetEnv.Env.Load(envFile);
        }

        var services = new ServiceCollection();

        // Register services
        services.AddSingleton<Logger>();
        services.AddSingleton<TipsConfig>();
        services.AddSingleton<FootballApiClient>();
        services.AddSingleton<AnnouncementService>();
        services.AddSingleton<CouponEvaluator>();
        services.AddSingleton<MessageHandler>();
        services.AddSingleton<ScorePollerService>();
        services.AddSingleton<BotHost>();

        var provider = services.BuildServiceProvider();

        var bot = provider.GetRequiredService<BotHost>();
        await bot.RunAsync();

        // Keep alive
        await Task.Delay(-1);
    }

    private static string? FindEnvFile(string startPath, string fileName)
    {
        try
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        catch { }

        return null;
    }
}