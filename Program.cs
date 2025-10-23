using Discord;
using Discord.WebSocket;
using DotNetEnv;
using System.Net.Http;
using System.Text.Json;
using System.Timers;
class Program
{
    private DiscordSocketClient _client;
    private readonly HttpClient _http = new HttpClient();
    private readonly Dictionary<int, (int Home, int Away)> lastScores = new();
    bool isFirstMessage = true;

    private readonly string Token;
    private readonly ulong ChannelId;
    private readonly string ApiKey;

    // Kolla https://dashboard.api-football.com/soccer/ids/teams för korrekta lagnamn i APIt
    List<TipsMatch> tipsRad = new()
    {
        new TipsMatch { Number = 1, HomeTeam = "Fulham", AwayTeam = "Arsenal", HomeKey = "Fulham", AwayKey = "Arsenal", Tip = "2" },
        new TipsMatch { Number = 2, HomeTeam = "Brighton", AwayTeam = "Newcastle", HomeKey = "Brighton", AwayKey = "Newcastle", Tip = "1X" },
        new TipsMatch { Number = 3, HomeTeam = "Manchester City", AwayTeam = "Everton", HomeKey = "Manchester City", AwayKey = "Everton", Tip = "1" },
        new TipsMatch { Number = 4, HomeTeam = "Burnley", AwayTeam = "Leeds", HomeKey = "Burnley", AwayKey = "Leeds", Tip = "1" },
        new TipsMatch { Number = 5, HomeTeam = "Crystal Palace", AwayTeam = "Bournemouth", HomeKey = "Crystal Palace", AwayKey = "Bournemouth", Tip = "12" },
        new TipsMatch { Number = 6, HomeTeam = "Sunderland", AwayTeam = "Wolverhampton", HomeKey = "Sunderland", AwayKey = "Wolves", Tip = "X2" },
        new TipsMatch { Number = 7, HomeTeam = "Birmingham", AwayTeam = "Hull", HomeKey = "Birmingham", AwayKey = "Hull City", Tip = "12" },
        new TipsMatch { Number = 8, HomeTeam = "Charlton", AwayTeam = "Sheffield W", HomeKey = "Charlton", AwayKey = "Sheffield Wednesday", Tip = "1" },
        new TipsMatch { Number = 9, HomeTeam = "Coventry", AwayTeam = "Blackburn", HomeKey = "Coventry", AwayKey = "Blackburn", Tip = "1" },
        new TipsMatch { Number = 10, HomeTeam = "Norwich", AwayTeam = "Bristol City", HomeKey = "Norwich", AwayKey = "Bristol City", Tip = "X2" },
        new TipsMatch { Number = 11, HomeTeam = "Sheffield U", AwayTeam = "Watford", HomeKey = "Sheffield Utd", AwayKey = "Watford", Tip = "1X2" },
        new TipsMatch { Number = 12, HomeTeam = "Stoke", AwayTeam = "Wrexham", HomeKey = "Stoke City", AwayKey = "Wrexham", Tip = "12" },
        new TipsMatch { Number = 13, HomeTeam = "West Bromwich", AwayTeam = "Preston", HomeKey = "West Brom", AwayKey = "Preston", Tip = "1" },
    };
    
    public Program()
    {
        // Load environment variables from .env file
        Env.Load();

        Token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        ApiKey = Environment.GetEnvironmentVariable("FOOTBALL_API_KEY");
        ChannelId = ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID_PROD"));
    }
    public static async Task Main() => await new Program().MainAsync();

    public async Task MainAsync()
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
        });
        _client.Log += LogAsync;
        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, Token);
        await _client.StartAsync();

        var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync())
        {
            await CheckScores();
        }

        await Task.Delay(-1); // Keep running
    }

private static readonly string[] IgnoredStatuses = ["Match Finished", "Halftime"];
private async Task CheckScores()
{
    try
    {
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("x-apisports-key", ApiKey);

        var channel = _client.GetChannel(ChannelId) as IMessageChannel;
        List<Match> allLive = await FetchAllLiveMatches();
        Log("Fetched matches");

        foreach (var tips in tipsRad)
        {
            tips.Match = allLive.FirstOrDefault(m =>
                string.Equals(m.HomeTeam, tips.HomeKey, StringComparison.OrdinalIgnoreCase)
                && !IgnoredStatuses.Contains(m.Status));

            if (tips.Match == null) continue;

            await ProcessMatchUpdate(channel, tips);
        }
    }
    catch (Exception ex)
    {
        Log($"❌ Error fetching scores: {ex.Message}");
    }
}

#region Match Handling

private async Task ProcessMatchUpdate(IMessageChannel channel, TipsMatch tm)
{
    Match match = tm.Match;
    Log($"✅ Checked match {tm.Number}: {match.HomeTeam} - {match.AwayTeam}");

    if (!lastScores.TryGetValue(match.Id, out var prevScore))
    {
        // First time we see this match
        lastScores[match.Id] = (match.HomeGoals, match.AwayGoals);
        return;
    }

    // Home team scored
    if (match.HomeGoals > prevScore.Home)
    {
        var latestGoal = await FetchLatestGoal(match.Id);
        await AnnounceGoal(channel, match, true, latestGoal, tm.Tip);
    }

    // Away team scored
    if (match.AwayGoals > prevScore.Away)
    {
        var latestGoal = await FetchLatestGoal(match.Id);
        await AnnounceGoal(channel, match, false, latestGoal, tm.Tip);
    }

    // Update state
    lastScores[match.Id] = (match.HomeGoals, match.AwayGoals);
}

private async Task AnnounceGoal(IMessageChannel channel, Match match, bool homeTeamScored, MatchEvent latestGoal, string tip)
{
    //Kolla om målet var åt rätt heller fel håll.
    string emoji = tip.Contains(match.Symbol) ? "✅" : "❌";
    string scoreDisplay = homeTeamScored ? $"**{match.HomeGoals}** - {match.AwayGoals}" : $"{match.HomeGoals} - **{match.AwayGoals}**"; 

    await channel.SendMessageAsync($"⚽{emoji} Mål! {match.HomeTeam} {scoreDisplay} {match.AwayTeam} {match.Elapsed}");
}

#endregion

#region Command Handling

private static readonly HashSet<ulong> AllowedUsers = new()
{
    186226697471787009, // Wontoz
    267355126069460993, // Wibb
    314039635451969536  // Ek
};
private const ulong ViktorId = 691946498791047228;
private const ulong AndersId = 1186441741868474398;

private async Task HandleMessageAsync(SocketMessage message)
{
    if (message.Author.IsBot) return;

    if (message.Author.Id == AndersId && message.Content.StartsWith("!"))
    {
        await message.Channel.SendMessageAsync("He");
        return;
    }

    if (message.Content.Equals("!status", StringComparison.OrdinalIgnoreCase))
    {
        if (!AllowedUsers.Contains(message.Author.Id))
        {
            await message.Channel.SendMessageAsync("I är icke med på stryket, i har inget att se här.");

            if (message.Author.Id == ViktorId)
            {
                await Task.Delay(5000);
                await message.Channel.SendMessageAsync("Särkilt inte du Viktor.");
            }
            return;
        }

        int correct = CheckTipsRad();
        string msg = isFirstMessage
            ? $"Just nu har vi {correct} rätt! Körvi"
            : $"Just nu har vi {correct} rätt!";

        await message.Channel.SendMessageAsync(msg);
        isFirstMessage = false;
    }
}

#endregion

#region API Helpers

private static int GetIntHelper(JsonElement el)
{
    if (el.ValueKind == JsonValueKind.Number)
        return el.GetInt32();

    if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var val))
    {
        return val;
    }
    return 0;
}

private async Task<List<Match>> FetchAllLiveMatches()
{
    string url = $"https://v3.football.api-sports.io/fixtures?live=all";
    string res = await _http.GetStringAsync(url);
    var doc = JsonDocument.Parse(res);

    return doc.RootElement.GetProperty("response")
        .EnumerateArray()
        .Select(m => new Match
        {
            Id = m.GetProperty("fixture").GetProperty("id").GetInt32(),
            Status = m.GetProperty("fixture").GetProperty("status").GetProperty("long").GetString(),
            HomeTeam = m.GetProperty("teams").GetProperty("home").GetProperty("name").GetString(),
            AwayTeam = m.GetProperty("teams").GetProperty("away").GetProperty("name").GetString(),
            HomeGoals = GetIntHelper(m.GetProperty("goals").GetProperty("home")),
            AwayGoals = GetIntHelper(m.GetProperty("goals").GetProperty("away")),
            Elapsed = GetIntHelper(m.GetProperty("fixture").GetProperty("status").GetProperty("elapsed"))
        })
        .ToList();
}

private async Task<List<MatchEvent>> FetchMatchEvents(int matchId, string type, string detailFilter = null)
{
    string url = $"https://v3.football.api-sports.io/fixtures/events?fixture={matchId}";
    string res = await _http.GetStringAsync(url);
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
                        extra.ValueKind != JsonValueKind.Null && extra.GetInt32() > 0
                        ? $"+{extra.GetInt32()}"
                        : ""
        })
        .ToList();
}

private async Task<MatchEvent> FetchLatestGoal(int matchId)
{
    var events = await FetchMatchEvents(matchId, "Goal");

    return events
        .OrderByDescending(e => e.Minute)
        .ThenByDescending(e => e.ExtraTime)
        .FirstOrDefault();
}

private void Log(string message) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    #endregion

    #region Tipsrad Helpers
private static string GetTipsResult(Match match)
{
    if (match.HomeGoals > match.AwayGoals) return "1";
    if (match.HomeGoals < match.AwayGoals) return "2";
    return "X";
}

private int CheckTipsRad()
{
    int correctMatches = 0;

    foreach (var tips in tipsRad)
    {
        if ((tips.Match == null && tips.Tip.Contains("X")) || (tips.Match != null && tips.Tip.Contains(tips.Match.Symbol)))
        {
            correctMatches++;
        }
    }

    return correctMatches;
}

#endregion

private Task LogAsync(LogMessage msg)
{
    Log(msg.ToString());
    return Task.CompletedTask;
}
}

#region Data Models

public record class TipsMatch
{
    public int Number { get; init; }
    public string HomeTeam { get; init; }
    public string AwayTeam { get; init; }
    public string HomeKey { get; init; }
    public string AwayKey { get; init; }
    public string Tip { get; init; }
    public Match Match { get; set; }
}

public record Match
{
    public int Id { get; init; }
    public string Status { get; init; }
    public string HomeTeam { get; init; }
    public string AwayTeam { get; init; }
    public int HomeGoals { get; init; }
    public int AwayGoals { get; init; }
    public int Elapsed { get; init; }
    public string Score => $"{HomeGoals} - {AwayGoals}";
    public string Symbol
    {
        get
        {
            if (HomeGoals > AwayGoals) return "1";
            if (HomeGoals < AwayGoals) return "2";
            return "X";
        }
    }
}

public record MatchEvent
{
    public string Player { get; init; }
    public string Team { get; init; }
    public int Minute { get; init; }
    public string ExtraTime { get; init; }
}

#endregion
