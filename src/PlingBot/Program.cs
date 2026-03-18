namespace PlingBot;
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
        bool testMode = args.Any(a =>
            string.Equals(a, "-TestMode", StringComparison.OrdinalIgnoreCase));

        string? envFile = null;

        var cwdCandidate = Path.Combine(Environment.CurrentDirectory, ".env");
        if (File.Exists(cwdCandidate)) envFile = cwdCandidate;

        if (envFile == null) envFile = FindEnvFile(AppContext.BaseDirectory, ".env");

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

        services.AddSingleton<Logger>();
        services.AddSingleton<TipsConfig>();
        services.AddSingleton<FootballApiClient>();
        services.AddSingleton<AnnouncementService>();
        services.AddSingleton<CouponEvaluator>();
        services.AddSingleton<MessageHandler>();
        services.AddSingleton<TestService>();
        services.AddSingleton<ScorePollerService>();

        services.AddSingleton(new BotOptions
        {
            TestMode = testMode
        });

        services.AddSingleton<BotHost>();

        var provider = services.BuildServiceProvider();

        var bot = provider.GetRequiredService<BotHost>();
        await bot.RunAsync();

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