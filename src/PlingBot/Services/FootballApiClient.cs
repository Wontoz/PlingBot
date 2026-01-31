namespace PlingBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using PlingBot.Models;
using PlingBot.Utils;

public class FootballApiClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Logger _logger;

    public FootballApiClient(Logger logger)
    {
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri("https://v3.football.api-sports.io/") };

        _apiKey = Environment.GetEnvironmentVariable("FOOTBALL_API_KEY")
            ?? throw new InvalidOperationException("FOOTBALL_API_KEY missing");

        _http.DefaultRequestHeaders.Add("x-apisports-key", _apiKey);
    }

    public async Task<List<Match>> FetchTodaysMatchesAsync()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        string todayRes = await _http.GetStringAsync($"fixtures?date={today}");
        var doc = JsonDocument.Parse(todayRes);

        return doc.RootElement.GetProperty("response")
            .EnumerateArray()
            .Select(m => new Match
            {
                Id = m.GetProperty("fixture").GetProperty("id").GetInt32(),
                Status = m.GetProperty("fixture").GetProperty("status").GetProperty("long").GetString(),
                HomeTeam = m.GetProperty("teams").GetProperty("home").GetProperty("name").GetString(),
                AwayTeam = m.GetProperty("teams").GetProperty("away").GetProperty("name").GetString(),
                HomeGoals = GetInt(m.GetProperty("goals").GetProperty("home")),
                AwayGoals = GetInt(m.GetProperty("goals").GetProperty("away")),
                Elapsed = GetInt(m.GetProperty("fixture").GetProperty("status").GetProperty("elapsed"))
            })
            .ToList();
    }


    public async Task<List<Match>> FetchAllLiveMatchesAsync()
    {
        string live = await _http.GetStringAsync("fixtures?live=all");
        var doc = JsonDocument.Parse(live);

        return doc.RootElement.GetProperty("response")
            .EnumerateArray()
            .Select(m => new Match
            {
                Id = m.GetProperty("fixture").GetProperty("id").GetInt32(),
                Status = m.GetProperty("fixture").GetProperty("status").GetProperty("long").GetString(),
                HomeTeam = m.GetProperty("teams").GetProperty("home").GetProperty("name").GetString(),
                AwayTeam = m.GetProperty("teams").GetProperty("away").GetProperty("name").GetString(),
                HomeGoals = GetInt(m.GetProperty("goals").GetProperty("home")),
                AwayGoals = GetInt(m.GetProperty("goals").GetProperty("away")),
                Elapsed = GetInt(m.GetProperty("fixture").GetProperty("status").GetProperty("elapsed"))
            })
            .ToList();
    }

    public async Task<MatchEvent?> FetchLatestGoalAsync(int matchId)
    {
        var events = await FetchMatchEventsAsync(matchId, "Goal");
        return events
            .OrderByDescending(e => e.Minute)
            .ThenByDescending(e => ParseExtraTime(e.ExtraTime))
            .FirstOrDefault();
    }

    private async Task<List<MatchEvent>> FetchMatchEventsAsync(int matchId, string type, string detailFilter = null)
    {
        
        string res = await _http.GetStringAsync($"fixtures/events?fixture={matchId}");
        var doc = JsonDocument.Parse(res);

        return doc.RootElement.GetProperty("response")
            .EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == type &&
                    (detailFilter == null || e.GetProperty("detail").GetString() == detailFilter))
            .Select(e => new MatchEvent
            {
                Player = e.GetProperty("player").GetProperty("name").GetString() ?? "Unknown",
                Team = e.GetProperty("team").GetProperty("name").GetString(),
                Minute = e.GetProperty("time").GetProperty("elapsed").GetInt32(),
                ExtraTime = e.GetProperty("time").TryGetProperty("extra", out var extra) &&
                            extra.ValueKind != JsonValueKind.Null && GetInt(extra) > 0
                            ? $"+{GetInt(extra)}"
                            : ""
            })
            .ToList();
    }

    private static int GetInt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetInt32();
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int v)) return v;
        return 0;
    }

    private static int ParseExtraTime(string? s) =>
        int.TryParse(s?.TrimStart('+'), out int n) ? n : 0;
}