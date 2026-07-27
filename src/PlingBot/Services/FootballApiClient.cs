namespace PlingBot.Services;

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PlingBot.Models;
using PlingBot.Utils;

public record FixtureBatchResult(
    Match Match,
    List<MatchEvent> Events,
    MatchStatistics? Statistics,
    TeamLineup? HomeLineup,
    TeamLineup? AwayLineup);

public record InjuryInfo(
    int FixtureId,
    int PlayerId,
    string PlayerName,
    string PlayerType,
    string? Reason,
    int TeamId,
    string TeamName);

public class FootballApiClient
{
    private readonly HttpClient _http;
    private readonly Logger _logger;
    private readonly ApiUsageTracker _usageTracker;
    private readonly SemaphoreSlim _rateLimitGate = new(1, 1);
    private readonly TimeSpan _minCallInterval;
    private DateTime _lastCallUtc = DateTime.MinValue;
    private int? _perMinuteLimit;
    private int? _dailyLimit;
    private int? _dailyRemaining;
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

        int minIntervalMs = int.TryParse(Environment.GetEnvironmentVariable("API_MIN_CALL_INTERVAL_MS"), out var ms)
            ? ms
            : 300;
        _minCallInterval = TimeSpan.FromMilliseconds(minIntervalMs);

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

    public async Task<Match?> FetchFixtureByIdAsync(int fixtureId)
    {
        string json = await GetApiJsonAsync($"fixtures?id={fixtureId}", "fixtures/id");
        using var doc = JsonDocument.Parse(json);

        if (TryLogApiErrors(doc.RootElement, $"fixtures id={fixtureId}"))
            return null;

        return doc.RootElement.GetProperty("response")
            .EnumerateArray()
            .Select(CreateMatchFromJson)
            .FirstOrDefault();
    }

    // Fetches all coupon fixtures in one call, returning match data, events, statistics and
    // lineups together. Replaces the old live-overlay + per-fixture events/stats pattern:
    // instead of ?live=all + N×?statistics + N×?events we pay for exactly 1 call per tick.
    public async Task<List<FixtureBatchResult>> FetchCouponFixturesBatchAsync(List<int> ids)
    {
        if (ids.Count == 0)
            return [];

        string idsParam = string.Join("-", ids);
        string json = await GetApiJsonAsync($"fixtures?ids={idsParam}", "fixtures/batch");
        using var doc = JsonDocument.Parse(json);

        if (TryLogApiErrors(doc.RootElement, "fixtures/batch"))
            return [];

        var results = new List<FixtureBatchResult>();
        foreach (var item in doc.RootElement.GetProperty("response").EnumerateArray())
        {
            var match = CreateMatchFromJson(item);

            var events = item.TryGetProperty("events", out var eventsElem) && eventsElem.ValueKind == JsonValueKind.Array
                ? eventsElem.EnumerateArray()
                    .Select(e => MapToMatchEvent(e, match.Id))
                    .Where(e => e != null)
                    .Cast<MatchEvent>()
                    .ToList()
                : new List<MatchEvent>();

            MatchStatistics? stats = null;
            if (item.TryGetProperty("statistics", out var statsElem) && statsElem.ValueKind == JsonValueKind.Array)
            {
                var statsItems = statsElem.EnumerateArray().ToList();
                if (statsItems.Count >= 2)
                    stats = new MatchStatistics
                    {
                        Home = ParseTeamStatistics(statsItems[0]),
                        Away = ParseTeamStatistics(statsItems[1])
                    };
            }

            TeamLineup? homeLineup = null;
            TeamLineup? awayLineup = null;
            if (item.TryGetProperty("lineups", out var lineupsElem) && lineupsElem.ValueKind == JsonValueKind.Array)
            {
                var lineupItems = lineupsElem.EnumerateArray().Select(ParseTeamLineup).ToList();
                if (lineupItems.Count >= 2)
                {
                    homeLineup = lineupItems[0];
                    awayLineup = lineupItems[1];
                }
            }

            results.Add(new FixtureBatchResult(match, events, stats, homeLineup, awayLineup));
        }

        return results;
    }

    private static TeamLineup ParseTeamLineup(JsonElement element)
    {
        string teamName = element.TryGetProperty("team", out var team) &&
                          team.TryGetProperty("name", out var name)
            ? name.GetString() ?? ""
            : "";

        string? formation = element.TryGetProperty("formation", out var formationElem)
            ? formationElem.GetString()
            : null;

        string? coachName = null;
        string? coachPhoto = null;
        if (element.TryGetProperty("coach", out var coachElem))
        {
            coachName = coachElem.TryGetProperty("name", out var coachNameElem) ? coachNameElem.GetString() : null;
            coachPhoto = coachElem.TryGetProperty("photo", out var coachPhotoElem) ? coachPhotoElem.GetString() : null;
        }

        return new TeamLineup
        {
            TeamName = teamName,
            Formation = formation,
            CoachName = coachName,
            CoachPhoto = coachPhoto,
            StartXI = ParseLineupPlayers(element, "startXI"),
            Substitutes = ParseLineupPlayers(element, "substitutes")
        };
    }

    private static List<LineupPlayer> ParseLineupPlayers(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<LineupPlayer>();
        foreach (var item in arr.EnumerateArray())
        {
            if (!item.TryGetProperty("player", out var p))
                continue;

            result.Add(new LineupPlayer
            {
                Id = p.TryGetProperty("id", out var idElem) ? GetInt(idElem) : 0,
                Name = p.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : "",
                Number = p.TryGetProperty("number", out var numElem) ? GetNullableInt(numElem) : null,
                Position = p.TryGetProperty("pos", out var posElem) ? posElem.GetString() : null
            });
        }
        return result;
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

    public async Task<List<H2HFixture>> FetchHeadToHeadAsync(int homeTeamId, int awayTeamId)
    {
        string json = await GetApiJsonAsync($"fixtures/headtohead?h2h={homeTeamId}-{awayTeamId}&last=5", "fixtures/h2h");
        using var doc = JsonDocument.Parse(json);

        if (TryLogApiErrors(doc.RootElement, $"fixtures/h2h {homeTeamId}-{awayTeamId}"))
            return [];

        var result = new List<H2HFixture>();
        foreach (var item in doc.RootElement.GetProperty("response").EnumerateArray())
        {
            try
            {
                var fixture = item.GetProperty("fixture");
                var teams   = item.GetProperty("teams");
                var goals   = item.GetProperty("goals");
                var status  = fixture.GetProperty("status");

                string statusShort = status.TryGetProperty("short", out var ss) ? ss.GetString() ?? "" : "";
                int homeGoals = GetInt(goals.GetProperty("home"));
                int awayGoals = GetInt(goals.GetProperty("away"));

                if ((statusShort == "AET" || statusShort == "PEN") &&
                    item.TryGetProperty("score", out var scoreElem) &&
                    scoreElem.TryGetProperty("fulltime", out var ftElem))
                {
                    int? ftH = ftElem.TryGetProperty("home", out var ftHElem) ? GetNullableInt(ftHElem) : null;
                    int? ftA = ftElem.TryGetProperty("away", out var ftAElem) ? GetNullableInt(ftAElem) : null;
                    if (ftH.HasValue && ftA.HasValue) { homeGoals = ftH.Value; awayGoals = ftA.Value; }
                }

                var home = teams.GetProperty("home");
                var away = teams.GetProperty("away");

                result.Add(new H2HFixture
                {
                    FixtureId    = GetInt(fixture.GetProperty("id")),
                    Date         = fixture.GetProperty("date").GetDateTime(),
                    HomeTeamId   = home.TryGetProperty("id",   out var hid) ? GetInt(hid) : 0,
                    HomeTeam     = home.TryGetProperty("name", out var hn)  ? hn.GetString() ?? "" : "",
                    HomeTeamLogo = home.TryGetProperty("logo", out var hl)  ? hl.GetString() : null,
                    HomeGoals    = homeGoals,
                    AwayTeamId   = away.TryGetProperty("id",   out var aid) ? GetInt(aid) : 0,
                    AwayTeam     = away.TryGetProperty("name", out var an)  ? an.GetString() ?? "" : "",
                    AwayTeamLogo = away.TryGetProperty("logo", out var al)  ? al.GetString() : null,
                    AwayGoals    = awayGoals,
                    StatusShort  = statusShort,
                });
            }
            catch { }
        }
        return result;
    }

    public async Task<List<InjuryInfo>> FetchInjuriesAsync(List<int> fixtureIds)
    {
        if (fixtureIds.Count == 0) return [];

        string idsParam = string.Join("-", fixtureIds);
        string json = await GetApiJsonAsync($"injuries?ids={idsParam}", "injuries/batch");
        using var doc = JsonDocument.Parse(json);

        if (TryLogApiErrors(doc.RootElement, "injuries/batch"))
            return [];

        var result = new List<InjuryInfo>();
        foreach (var item in doc.RootElement.GetProperty("response").EnumerateArray())
        {
            if (!item.TryGetProperty("player",  out var playerElem) ||
                !item.TryGetProperty("team",    out var teamElem)   ||
                !item.TryGetProperty("fixture", out var fixtureElem))
                continue;

            int fixtureId = fixtureElem.TryGetProperty("id",   out var fid)   ? fid.GetInt32()          : 0;
            int playerId  = playerElem.TryGetProperty("id",    out var pid)   ? pid.GetInt32()          : 0;
            string name   = playerElem.TryGetProperty("name",  out var pname) ? pname.GetString() ?? "" : "";
            string type   = playerElem.TryGetProperty("type",  out var ptype) ? ptype.GetString() ?? "" : "";
            string? reason = playerElem.TryGetProperty("reason", out var pr) && pr.ValueKind != JsonValueKind.Null
                ? pr.GetString() : null;
            int teamId    = teamElem.TryGetProperty("id",   out var tid)   ? tid.GetInt32()          : 0;
            string team   = teamElem.TryGetProperty("name", out var tname) ? tname.GetString() ?? "" : "";

            if (fixtureId > 0 && playerId > 0)
                result.Add(new InjuryInfo(fixtureId, playerId, name, type, reason, teamId, team));
        }

        return result;
    }

    // Hits the /status endpoint, which does NOT count against the daily quota. Its response
    // headers carry the same x-ratelimit-* values as every other call, so this lets us learn
    // the account's real per-minute/daily limits before the startup burst even begins.
    public async Task FetchAndApplyAccountStatusAsync()
    {
        try
        {
            string json = await GetApiJsonAsync("status", "status");
            using var doc = JsonDocument.Parse(json);

            if (TryLogApiErrors(doc.RootElement, "status"))
                return;

            var requests = doc.RootElement.GetProperty("response").GetProperty("requests");
            int current = GetInt(requests.GetProperty("current"));
            int limitDay = GetInt(requests.GetProperty("limit_day"));

            _dailyLimit = limitDay;
            _dailyRemaining = limitDay - current;

            _logger.Log(
                $"API status: {current}/{limitDay} requests used today" +
                (_perMinuteLimit.HasValue ? $", {_perMinuteLimit}/min limit" : ""),
                ConsoleColor.DarkCyan);
        }
        catch (Exception ex)
        {
            _logger.Log($"Could not fetch API status: {ex.Message}", ConsoleColor.DarkYellow);
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

        string? leagueName = null;
        string? leagueFlag = null;
        string? leagueLogo = null;
        string? leagueRound = null;
        if (element.TryGetProperty("league", out var leagueElem))
        {
            leagueName = leagueElem.TryGetProperty("name", out var ln) ? ln.GetString() : null;
            leagueFlag = leagueElem.TryGetProperty("flag", out var lf) ? lf.GetString() : null;
            leagueLogo = leagueElem.TryGetProperty("logo", out var ll) ? ll.GetString() : null;
            leagueRound = leagueElem.TryGetProperty("round", out var lr) ? lr.GetString() : null;
        }

        string? venueName = null;
        if (fixtureElem.TryGetProperty("venue", out var venueElem))
            venueName = venueElem.TryGetProperty("name", out var vn) ? vn.GetString() : null;

        // For AET/PEN matches the top-level goals field is the final score (including ET/shootout).
        // score.fulltime is the score at 90 minutes, which is what the coupon cares about.
        int homeGoals = GetInt(goalsElem.GetProperty("home"));
        int awayGoals = GetInt(goalsElem.GetProperty("away"));
        if ((statusShort == "AET" || statusShort == "PEN") &&
            element.TryGetProperty("score", out var scoreElem) &&
            scoreElem.TryGetProperty("fulltime", out var ftElem))
        {
            int? ftH = ftElem.TryGetProperty("home", out var ftHElem) ? GetNullableInt(ftHElem) : null;
            int? ftA = ftElem.TryGetProperty("away", out var ftAElem) ? GetNullableInt(ftAElem) : null;
            if (ftH.HasValue && ftA.HasValue)
            {
                homeGoals = ftH.Value;
                awayGoals = ftA.Value;
            }
        }

        return new Match
        {
            Id = GetInt(fixtureElem.GetProperty("id")),
            Date = fixtureElem.GetProperty("date").GetDateTime(),
            Status = CreateStatus(statusShort, statusLong),

            HomeTeam = teamsElem.GetProperty("home").GetProperty("name").GetString() ?? "",
            AwayTeam = teamsElem.GetProperty("away").GetProperty("name").GetString() ?? "",

            HomeTeamId = GetNullableInt(teamsElem.GetProperty("home").GetProperty("id")),
            AwayTeamId = GetNullableInt(teamsElem.GetProperty("away").GetProperty("id")),
            LeagueName = leagueName,
            LeagueFlag = leagueFlag,
            LeagueLogo = leagueLogo,
            LeagueRound = leagueRound,
            VenueName = venueName,

            HomeGoals = homeGoals,
            AwayGoals = awayGoals,
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
        await ThrottleAsync();

        _usageTracker.Record(endpoint);
        using var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        UpdateRateLimitState(response.Headers);

        return await response.Content.ReadAsStringAsync();
    }

    // Reads the rate-limit headers every call sends back so the throttle can adapt to the
    // account's *actual* limits instead of a guessed constant, and so the bot can slow itself
    // down automatically as the daily quota runs low rather than just logging a warning.
    private void UpdateRateLimitState(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("x-ratelimit-requests-limit", out var dailyLimitValues) &&
            int.TryParse(dailyLimitValues.FirstOrDefault(), out int dailyLimit))
        {
            _dailyLimit = dailyLimit;
        }

        if (headers.TryGetValues("x-ratelimit-requests-remaining", out var dailyRemainingValues) &&
            int.TryParse(dailyRemainingValues.FirstOrDefault(), out int dailyRemaining))
        {
            _dailyRemaining = dailyRemaining;
            if (dailyRemaining < 50)
                _logger.Log($"⚠️ API daily quota low: {dailyRemaining} requests remaining", ConsoleColor.Red);
        }

        if (headers.TryGetValues("X-RateLimit-Limit", out var perMinuteLimitValues) &&
            int.TryParse(perMinuteLimitValues.FirstOrDefault(), out int perMinuteLimit) &&
            perMinuteLimit != _perMinuteLimit)
        {
            _perMinuteLimit = perMinuteLimit;
        }

        if (headers.TryGetValues("X-RateLimit-Remaining", out var minuteRemainingValues) &&
            int.TryParse(minuteRemainingValues.FirstOrDefault(), out int minuteRemaining) && minuteRemaining < 5)
        {
            _logger.Log($"⚠️ API per-minute quota low: {minuteRemaining} requests remaining this minute", ConsoleColor.Red);
        }
    }

    // Serializes calls and enforces a minimum gap between them so bursts (e.g. startup
    // backfill/fixture lookups) can't trip the per-minute rate limit. The gap itself adapts:
    // once the account's real per-minute limit is known (via response headers or /status) it
    // replaces the guessed API_MIN_CALL_INTERVAL_MS default, and it's stretched further as the
    // daily quota runs low so a slow afternoon doesn't get fully blocked before the day ends.
    private async Task ThrottleAsync()
    {
        await _rateLimitGate.WaitAsync();
        try
        {
            var interval = ComputeCallInterval();
            var wait = interval - (DateTime.UtcNow - _lastCallUtc);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);

            _lastCallUtc = DateTime.UtcNow;
        }
        finally
        {
            _rateLimitGate.Release();
        }
    }

    private TimeSpan ComputeCallInterval()
    {
        // 15% safety margin under the account's actual per-minute limit once known;
        // falls back to the configured default until the first response teaches us the real value.
        TimeSpan baseInterval = _perMinuteLimit is > 0
            ? TimeSpan.FromMilliseconds(60_000.0 / _perMinuteLimit.Value * 1.15)
            : _minCallInterval;

        double backoff = _dailyLimit is > 0 && _dailyRemaining.HasValue
            ? GetDailyQuotaBackoffFactor((double)_dailyRemaining.Value / _dailyLimit.Value)
            : 1.0;

        return baseInterval * backoff;
    }

    // Stretches the remaining daily quota out instead of burning through it at full speed
    // as it runs low — still keeps polling, just increasingly conservatively.
    private static double GetDailyQuotaBackoffFactor(double remainingFraction)
    {
        if (remainingFraction < 0.05) return 8.0;
        if (remainingFraction < 0.10) return 4.0;
        if (remainingFraction < 0.20) return 2.0;
        return 1.0;
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
