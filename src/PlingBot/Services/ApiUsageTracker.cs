namespace PlingBot.Services;

using PlingBot.Utils;

public class ApiUsageTracker
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);
    private readonly Logger _logger;
    private readonly Queue<DateTime> recentCalls = new();
    private readonly Dictionary<string, int> totalByEndpoint = new();
    private readonly object syncLock = new();
    private DateTime lastReportUtc = DateTime.MinValue;
    private int totalCalls;

    public ApiUsageTracker(Logger logger)
    {
        _logger = logger;
    }

    public void Record(string endpoint)
    {
        lock (syncLock)
        {
            DateTime now = DateTime.UtcNow;
            recentCalls.Enqueue(now);
            totalCalls++;

            if (!totalByEndpoint.TryAdd(endpoint, 1))
                totalByEndpoint[endpoint]++;

            PruneRecentCalls(now);

            if (now - lastReportUtc >= ReportInterval)
            {
                lastReportUtc = now;
                LogUsage(now);
            }
        }
    }

    private void PruneRecentCalls(DateTime now)
    {
        while (recentCalls.Count > 0 && now - recentCalls.Peek() > TimeSpan.FromMinutes(1))
            recentCalls.Dequeue();
    }

    private void LogUsage(DateTime now)
    {
        int callsLastMinute = recentCalls.Count;
        double projectedDaily = callsLastMinute * 60 * 24;

        var topEndpoints = totalByEndpoint
            .OrderByDescending(pair => pair.Value)
            .Take(3)
            .Select(pair => $"{pair.Key}: {pair.Value}");

    }
}
