namespace PlingBot.Services;

using PlingBot.Utils;

public class ApiUsageTracker
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);
    private readonly Logger _logger;
    private readonly Queue<DateTime> _recentCalls = new();
    private readonly Dictionary<string, int> _totalByEndpoint = new();
    private readonly object _sync = new();
    private DateTime _lastReportUtc = DateTime.MinValue;
    private int _totalCalls;

    public ApiUsageTracker(Logger logger)
    {
        _logger = logger;
    }

    public void Record(string endpoint)
    {
        lock (_sync)
        {
            DateTime now = DateTime.UtcNow;
            _recentCalls.Enqueue(now);
            _totalCalls++;

            if (!_totalByEndpoint.TryAdd(endpoint, 1))
                _totalByEndpoint[endpoint]++;

            PruneRecentCalls(now);

            if (now - _lastReportUtc >= ReportInterval)
            {
                _lastReportUtc = now;
                LogUsage(now);
            }
        }
    }

    private void PruneRecentCalls(DateTime now)
    {
        while (_recentCalls.Count > 0 && now - _recentCalls.Peek() > TimeSpan.FromMinutes(1))
            _recentCalls.Dequeue();
    }

    private void LogUsage(DateTime now)
    {
        int callsLastMinute = _recentCalls.Count;
        double projectedDaily = callsLastMinute * 60 * 24;

        var topEndpoints = _totalByEndpoint
            .OrderByDescending(pair => pair.Value)
            .Take(3)
            .Select(pair => $"{pair.Key}: {pair.Value}");

        _logger.Log(
            $"API usage: {callsLastMinute}/min, {_totalCalls} since startup, projected {projectedDaily:0}/day. Top: {string.Join(", ", topEndpoints)}",
            ConsoleColor.DarkCyan);
    }
}
