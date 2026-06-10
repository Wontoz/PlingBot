namespace PlingBot.Services;

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
    private readonly ApiUsageTracker _usageTracker;
    private readonly Dictionary<int, string?> _playerNameCache = new();
    private static readonly Dictionary<string, (string Type, string Description)> StatusMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TBD"] = ("Scheduled", "Scheduled but date and time are not known"),
        ["NS"] = ("Scheduled", ""),
        ["1H"] = ("In Play", "First half in play"),
        ["HT"] = ("In Play", "Finished in the regular time"),
        ["2H"] = ("In Play", "Second half in play"),
        ["ET"] = ("In Play", "Extra time in play"),
        ["BT"] = ("In Play", "Break during extra time"),
        ["P"] = ("In Play", "Penalty played after extra time"),
        ["SUSP"] = ("In Play", "Suspended by referee's decision, may be rescheduled another day"),
        ["INT"] = ("In Play", "Interrupted by referee's decision, should resume in a few minutes"),
        ["FT"] = ("Finished", "Finished in the regular time"),
        ["AET"] = ("Finished", "Finished after extra time without going to the penalty shootout"),
        ["PEN"] = ("Finished", "Finished after the penalty shootout"),
        ["PST"] = ("Postponed", "Postponed to another day"),
        ["CANC"] = ("Cancelled", "Cancelled, match will not be played"),
        ["ABD"] = ("Abandoned", "Abandoned for various reasons"),
        ["AWD"] = ("Not Played", ""),
        ["WO"] = ("Not Played", "Victory by forfeit or absence of competitor"),
        ["LIVE"] = ("In Play", "Fixture in progress, elapsed time not available")
    };

    public FootballApiClient(Logger logger, ApiUsageTracker usageTracker)
    {
        _logger = logger;
        _usageTracker = usageTracker;

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
        string json = await GetApiJsonAsync($"fixtures?date={dateString}", "fixtures/date");
        using var doc = JsonDocument.Parse(json);

        if (TryLogApiErrors(doc.RootElement, $"fixtures date={dateString}"))
            return [];

        return doc.RootElement.GetProperty("response")
            .EnumerateArray()
            .Select(CreateMatchFromJson)
            .ToList();
    }

    public async Task<List<Match>> FetchAllLiveMatchesAsync()
    {
        string json = await GetApiJsonAsync("fixtures?live=all", "fixtures/live");
        using var doc = JsonDocument.Parse(json);

        if (TryLogApiErrors(doc.RootElement, "fixtures/live"))
            return [];

        return doc.RootElement.GetProperty("response")
            .EnumerateArray()
            .Select(CreateMatchFromJson)
            .ToList();
    }

    public async Task<List<MatchEvent>> FetchMatchEventsByTypeAsync(int matchId, string type)
    {
        var events = await FetchMatchEventsAsync(matchId);

        return events
            .Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<List<MatchEvent>> FetchMatchEventsAsync(int matchId)
    {
        try
        {
            string json = await GetApiJsonAsync($"fixtures/events?fixture={matchId}", "fixtures/events");
            using var doc = JsonDocument.Parse(json);

            if (TryLogApiErrors(doc.RootElement, $"fixture events {matchId}"))
                return [];

            if (!doc.RootElement.TryGetProperty("response", out var response) ||
                response.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return response.EnumerateArray()
                .Select(e => MapToMatchEvent(e, matchId))
                .Where(e => e != null)
                .Cast<MatchEvent>()
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            _logger.Log($"HTTP error fetching events for fixture {matchId}: {ex.Message}");
            return [];
        }
        catch (JsonException ex)
        {
            _logger.Log($"JSON parse error fetching events for fixture {matchId}: {ex.Message}");
            return [];
        }
        catch (Exception ex)
        {
            _logger.Log($"Unexpected error fetching events for fixture {matchId}: {ex.Message}");
            return [];
        }
    }

    public async Task<MatchStatistics?> FetchMatchStatisticsAsync(int fixtureId)
    {
        try
        {
            string json = await GetApiJsonAsync($"fixtures/statistics?fixture={fixtureId}", "fixtures/statistics");
            using var doc = JsonDocument.Parse(json);

            if (TryLogApiErrors(doc.RootElement, $"fixture statistics {fixtureId}"))
                return null;

            if (!doc.RootElement.TryGetProperty("response", out var response) ||
                response.ValueKind != JsonValueKind.Array)
                return null;

            var items = response.EnumerateArray().ToList();
            if (items.Count < 2)
                return null;

            return new MatchStatistics
            {
                Home = ParseTeamStatistics(items[0]),
                Away = ParseTeamStatistics(items[1])
            };
        }
        catch (Exception ex)
        {
            _logger.Log($"Error fetching statistics for fixture {fixtureId}: {ex.Message}", ConsoleColor.DarkYellow);
            return null;
        }
    }

    private static TeamStatistics ParseTeamStatistics(JsonElement element)
    {
        string teamName = element.TryGetProperty("team", out var team) &&
                          team.TryGetProperty("name", out var name)
            ? name.GetString() ?? ""
            : "";

        var stats = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (element.TryGetProperty("statistics", out var statsArray))
        {
            foreach (var stat in statsArray.EnumerateArray())
            {
                if (!stat.TryGetProperty("type", out var typeElem)) continue;
                string? type = typeElem.GetString();
                if (type == null) continue;

                stat.TryGetProperty("value", out var valueElem);
                string? value = valueElem.ValueKind switch
                {
                    JsonValueKind.Number => valueElem.GetInt32().ToString(),
                    JsonValueKind.String => valueElem.GetString(),
                    _ => null
                };

                stats[type] = value;
            }
        }

        return new TeamStatistics
        {
            TeamName = teamName,
            ShotsOnGoal = stats.GetValueOrDefault("Shots on Goal"),
            ShotsOffGoal = stats.GetValueOrDefault("Shots off Goal"),
            TotalShots = stats.GetValueOrDefault("Total Shots"),
            BlockedShots = stats.GetValueOrDefault("Blocked Shots"),
            ShotsInsideBox = stats.GetValueOrDefault("Shots insidebox"),
            ShotsOutsideBox = stats.GetValueOrDefault("Shots outsidebox"),
            Fouls = stats.GetValueOrDefault("Fouls"),
            CornerKicks = stats.GetValueOrDefault("Corner Kicks"),
            Offsides = stats.GetValueOrDefault("Offsides"),
            BallPossession = stats.GetValueOrDefault("Ball Possession"),
            YellowCards = stats.GetValueOrDefault("Yellow Cards"),
            RedCards = stats.GetValueOrDefault("Red Cards"),
            GoalkeeperSaves = stats.GetValueOrDefault("Goalkeeper Saves"),
            TotalPasses = stats.GetValueOrDefault("Total passes"),
            PassesAccurate = stats.GetValueOrDefault("Passes accurate"),
            PassesPercent = stats.GetValueOrDefault("Passes %")
        };
    }

    private MatchEvent? MapToMatchEvent(JsonElement e, int fixtureId)
    {
        try
        {
            var timeElem = e.GetProperty("time");
            var teamElem = e.GetProperty("team");
            var playerElem = e.TryGetProperty("player", out var p) ? p : default;
            var assistElem = e.TryGetProperty("assist", out var a) ? a : default;

            int elapsed = timeElem.TryGetProperty("elapsed", out var elapsedElem)
                ? GetInt(elapsedElem)
                : 0;

            int extra = timeElem.TryGetProperty("extra", out var extraElem)
                ? GetInt(extraElem)
                : 0;

            return new MatchEvent
            {
                FixtureId = fixtureId,
                Type = e.TryGetProperty("type", out var typeElem) ? typeElem.GetString() : null,
                Detail = e.TryGetProperty("detail", out var detailElem) ? detailElem.GetString() : null,
                PlayerId = playerElem.ValueKind != JsonValueKind.Undefined &&
                         playerElem.TryGetProperty("id", out var playerIdElem)
                    ? GetNullableInt(playerIdElem)
                    : null,
                Player = playerElem.ValueKind != JsonValueKind.Undefined &&
                         playerElem.TryGetProperty("name", out var nameElem)
                    ? nameElem.GetString()
                    : null,
                Team = teamElem.TryGetProperty("name", out var teamNameElem)
                    ? teamNameElem.GetString()
                    : null,
                TeamId = teamElem.TryGetProperty("id", out var teamIdElem)
                    ? GetNullableInt(teamIdElem)
                    : null,
                AssistId = assistElem.ValueKind != JsonValueKind.Undefined &&
                    assistElem.TryGetProperty("id", out var assistIdElem)
                    ? GetNullableInt(assistIdElem)
                    : null,
                Assist = assistElem.ValueKind != JsonValueKind.Undefined &&
                    assistElem.TryGetProperty("name", out var assistNameElem)
                    ? assistNameElem.GetString()
                    : null,
                Elapsed = elapsed,
                Extra = extra,
                Comments = e.TryGetProperty("comments", out var commentsElem)
                    ? commentsElem.GetString()
                    : null
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> FetchPlayerFullNameAsync(int playerId)
    {
        if (_playerNameCache.TryGetValue(playerId, out var cachedName))
            return cachedName;

        try
        {
            string json = await GetApiJsonAsync($"players/profiles?player={playerId}", "players/profiles");
            using var doc = JsonDocument.Parse(json);

            if (TryLogApiErrors(doc.RootElement, $"player profile {playerId}"))
            {
                _playerNameCache[playerId] = null;
                return null;
            }

            var player = doc.RootElement
                .GetProperty("response")
                .EnumerateArray()
                .Select(item => item.GetProperty("player"))
                .FirstOrDefault();

            if (player.ValueKind == JsonValueKind.Undefined)
            {
                _playerNameCache[playerId] = null;
                return null;
            }

            string firstName = player.TryGetProperty("firstname", out var firstNameElem)
                ? firstNameElem.GetString() ?? ""
                : "";
            string lastName = player.TryGetProperty("lastname", out var lastNameElem)
                ? lastNameElem.GetString() ?? ""
                : "";
            string fullName = $"{firstName} {lastName}".Trim();

            if (string.IsNullOrWhiteSpace(fullName) &&
                player.TryGetProperty("name", out var nameElem))
            {
                fullName = nameElem.GetString() ?? "";
            }

            _playerNameCache[playerId] = string.IsNullOrWhiteSpace(fullName) ? null : fullName;
            return _playerNameCache[playerId];
        }
        catch (Exception ex)
        {
            _logger.Log($"Could not fetch player profile {playerId}: {ex.Message}", ConsoleColor.DarkYellow);
            _playerNameCache[playerId] = null;
            return null;
        }
    }

    private static Match CreateMatchFromJson(JsonElement element)
    {
        var fixtureElem = element.GetProperty("fixture");
        var statusElem = fixtureElem.GetProperty("status");
        var teamsElem = element.GetProperty("teams");
        var goalsElem = element.GetProperty("goals");

        string statusShort = statusElem.GetProperty("short").GetString() ?? "UNK";
        string statusLong = statusElem.GetProperty("long").GetString() ?? "Unknown";

        return new Match
        {
            Id = GetInt(fixtureElem.GetProperty("id")),
            Date = fixtureElem.GetProperty("date").GetDateTime(),
            Status = CreateStatus(statusShort, statusLong),

            HomeTeam = teamsElem.GetProperty("home").GetProperty("name").GetString() ?? "",
            AwayTeam = teamsElem.GetProperty("away").GetProperty("name").GetString() ?? "",

            HomeTeamId = GetNullableInt(teamsElem.GetProperty("home").GetProperty("id")),
            AwayTeamId = GetNullableInt(teamsElem.GetProperty("away").GetProperty("id")),

            HomeGoals = GetInt(goalsElem.GetProperty("home")),
            AwayGoals = GetInt(goalsElem.GetProperty("away")),
            Elapsed = GetInt(statusElem.GetProperty("elapsed")),
            Extra = GetInt(statusElem.GetProperty("extra"))
        };
    }

    private static Status CreateStatus(string statusShort, string statusLong)
    {
        if (!StatusMetadata.TryGetValue(statusShort, out var metadata))
            return new Status(statusShort, statusLong, "", "");

        return new Status(statusShort, statusLong, metadata.Type, metadata.Description);
    }

    private static int GetInt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int v))
            return v;

        return 0;
    }

    private static int? GetNullableInt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;

        if (el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int v))
            return v;

        return null;
    }

    private async Task<string> GetApiJsonAsync(string url, string endpoint)
    {
        _usageTracker.Record(endpoint);
        using var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        if (response.Headers.TryGetValues("x-ratelimit-requests-remaining", out var values) &&
            int.TryParse(values.FirstOrDefault(), out int remaining) && remaining < 50)
        {
            _logger.Log($"⚠️ API daily quota low: {remaining} requests remaining", ConsoleColor.Red);
        }

        return await response.Content.ReadAsStringAsync();
    }

    private bool TryLogApiErrors(JsonElement root, string context)
    {
        if (!root.TryGetProperty("errors", out var errors))
            return false;

        if (errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() == 0)
            return false;

        if (errors.ValueKind == JsonValueKind.Object && !errors.EnumerateObject().Any())
            return false;

        _logger.Log($"API error ({context}): {errors}", ConsoleColor.Red);
        return true;
    }
}
