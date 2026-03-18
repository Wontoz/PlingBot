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
    private readonly Logger _logger;
    private bool _hasFetchedOnce;

    public FootballApiClient(Logger logger)
    {
        _logger = logger;

        var baseUrl = Environment.GetEnvironmentVariable("FOOTBALL_API_URL")
            ?? throw new InvalidOperationException("FOOTBALL_API_URL missing");

        var apiKey = Environment.GetEnvironmentVariable("FOOTBALL_API_KEY")
            ?? throw new InvalidOperationException("FOOTBALL_API_KEY missing");

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl)
        };

        _http.DefaultRequestHeaders.Add("x-apisports-key", apiKey);
    }
    
    public async Task<List<Match>> FetchMatchesByDateAsync(DateTime date)
    {
        string dateString = date.ToString("yyyy-MM-dd");
        string url = $"fixtures?date={dateString}";

        string json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.GetProperty("response")
            .EnumerateArray()
            .Select(CreateMatchFromJson)
            .ToList();
    }

    public async Task<List<Match>> FetchTodaysMatchesAsync()
    {
        var today = DateTime.UtcNow.Date;

        if (!_hasFetchedOnce)
            _logger.Log($"Fetching today's fixtures: fixtures?date={today:yyyy-MM-dd}", ConsoleColor.DarkGray);

        _hasFetchedOnce = true;

        return await FetchMatchesByDateAsync(today);
    }

    public async Task<List<Match>> FetchAllLiveMatchesAsync()
    {
        string json = await _http.GetStringAsync("fixtures?live=all");
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.GetProperty("response")
            .EnumerateArray()
            .Select(CreateMatchFromJson)
            .ToList();
    }

    public async Task<List<MatchEvent>> FetchMatchEventsByTypeAsync(int matchId, string type)
    {
        try
        {
            string url = $"fixtures/events?fixture={matchId}&type={type.ToLowerInvariant()}";
            string json = await _http.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("response", out var response) ||
                response.ValueKind != JsonValueKind.Array)
            {
                return new List<MatchEvent>();
            }

            return response.EnumerateArray()
                .Select(MapToMatchEvent)
                .Where(e => e != null)
                .Cast<MatchEvent>()
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            _logger.Log($"HTTP error fetching {type} events for fixture {matchId}: {ex.Message}");
            return new List<MatchEvent>();
        }
        catch (JsonException ex)
        {
            _logger.Log($"JSON parse error fetching {type} events for fixture {matchId}: {ex.Message}");
            return new List<MatchEvent>();
        }
        catch (Exception ex)
        {
            _logger.Log($"Unexpected error fetching {type} events for fixture {matchId}: {ex.Message}");
            return new List<MatchEvent>();
        }
    }

    private MatchEvent? MapToMatchEvent(JsonElement e)
    {
        try
        {
            var timeElem = e.GetProperty("time");
            var teamElem = e.GetProperty("team");
            var playerElem = e.TryGetProperty("player", out var p) ? p : default;

            int extraTime = 0;
            if (timeElem.TryGetProperty("extra", out var extraElem) &&
                extraElem.ValueKind != JsonValueKind.Null)
            {
                extraTime = extraElem.GetInt32();
            }

            return new MatchEvent
            {
                Type = e.TryGetProperty("type", out var typeElem) ? typeElem.GetString() : null,
                Detail = e.TryGetProperty("detail", out var detailElem) ? detailElem.GetString() : null,
                Player = playerElem.ValueKind != JsonValueKind.Undefined &&
                         playerElem.TryGetProperty("name", out var nameElem)
                    ? nameElem.GetString()
                    : null,
                Team = teamElem.TryGetProperty("name", out var teamNameElem)
                    ? teamNameElem.GetString()
                    : null,
                Elapsed = timeElem.GetProperty("elapsed").GetInt32(),
                ExtraTime = extraTime
            };
        }
        catch
        {
            return null;
        }
    }

    private static Match CreateMatchFromJson(JsonElement element)
    {
        return new Match
        {
            Id = element.GetProperty("fixture").GetProperty("id").GetInt32(),
            Status = element.GetProperty("fixture").GetProperty("status").GetProperty("long").GetString() ?? "Unknown",

            HomeTeam = element.GetProperty("teams").GetProperty("home").GetProperty("name").GetString() ?? "",
            AwayTeam = element.GetProperty("teams").GetProperty("away").GetProperty("name").GetString() ?? "",

            HomeTeamId = element.GetProperty("teams").GetProperty("home").GetProperty("id").GetInt32(),
            AwayTeamId = element.GetProperty("teams").GetProperty("away").GetProperty("id").GetInt32(),

            HomeGoals = GetInt(element.GetProperty("goals").GetProperty("home")),
            AwayGoals = GetInt(element.GetProperty("goals").GetProperty("away")),
            Elapsed = GetInt(element.GetProperty("fixture").GetProperty("status").GetProperty("elapsed"))
        };
    }

    private static int GetInt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int v))
            return v;

        return 0;
    }
}