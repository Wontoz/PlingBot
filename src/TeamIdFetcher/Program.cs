using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

// Load .env by walking up from the binary until we find one
var dir = new DirectoryInfo(AppContext.BaseDirectory);
while (dir != null)
{
    var candidate = Path.Combine(dir.FullName, ".env");
    if (File.Exists(candidate))
    {
        DotNetEnv.Env.Load(candidate);
        Console.WriteLine($"Loaded env from: {candidate}");
        break;
    }
    dir = dir.Parent;
}

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var writeOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var baseUrl = Environment.GetEnvironmentVariable("FOOTBALL_API_URL")
    ?? throw new InvalidOperationException("FOOTBALL_API_URL missing");
var apiKey = Environment.GetEnvironmentVariable("FOOTBALL_API_KEY")
    ?? throw new InvalidOperationException("FOOTBALL_API_KEY missing");

var teamsFilePath = ResolveTeamsFilePath();

Console.WriteLine($"Loading teams from: {teamsFilePath}");
var json = await File.ReadAllTextAsync(teamsFilePath, Encoding.UTF8);
var teams = JsonSerializer.Deserialize<List<TeamRecord>>(json, jsonOptions) ?? [];

var nullIdTeams = teams.Where(t => t.Id == null).ToList();
Console.WriteLine($"Found {nullIdTeams.Count} teams with missing Id.\n");

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
http.DefaultRequestHeaders.Add("x-apisports-key", apiKey);

int autoFilled = 0;
int manualFilled = 0;
var stillMissing = new List<TeamRecord>();

foreach (var team in nullIdTeams)
{
    var results = await SearchTeam(http, team.ApiName);
    await Task.Delay(200); // be gentle with rate limits

    if (results.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.DarkRed;
        Console.WriteLine($"  NO RESULTS: {team.Name} (search: '{team.ApiName}')");
        Console.ResetColor();
        stillMissing.Add(team);
        continue;
    }

    var exactMatch = results.FirstOrDefault(r =>
        string.Equals(r.Name, team.ApiName, StringComparison.OrdinalIgnoreCase));

    if (results.Count == 1 || exactMatch != null)
    {
        var pick = exactMatch ?? results[0];
        team.Id = pick.Id;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  AUTO: {team.Name} → Id {team.Id} ({pick.Name}, {pick.Country})");
        Console.ResetColor();
        autoFilled++;
        continue;
    }

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  MULTIPLE ({results.Count}) for '{team.Name}' (search: '{team.ApiName}'):");
    Console.ResetColor();
    for (int i = 0; i < results.Count; i++)
        Console.WriteLine($"    [{i + 1}] {results[i].Name} (Id: {results[i].Id}, {results[i].Country})");
    Console.Write("  Choose [1-{0}] or [s]kip: ", results.Count);

    var input = Console.ReadLine()?.Trim().ToLower();
    if (input == "s" || string.IsNullOrEmpty(input))
    {
        Console.WriteLine("  Skipped.");
        stillMissing.Add(team);
    }
    else if (int.TryParse(input, out int choice) && choice >= 1 && choice <= results.Count)
    {
        team.Id = results[choice - 1].Id;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  SET: {team.Name} → Id {team.Id}");
        Console.ResetColor();
        manualFilled++;
    }
    else
    {
        Console.WriteLine("  Invalid input, skipping.");
        stillMissing.Add(team);
    }
}

Console.WriteLine($"\nDone. Auto: {autoFilled}, Manual: {manualFilled}, Still missing: {stillMissing.Count}");

if (stillMissing.Count > 0)
{
    Console.ForegroundColor = ConsoleColor.DarkRed;
    Console.WriteLine("\nStill missing Id:");
    foreach (var t in stillMissing)
        Console.WriteLine($"  {t.Name} (ApiName: '{t.ApiName}')");
    Console.ResetColor();
}
Console.WriteLine("Saving teams.json...");

var sorted = teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
var output = JsonSerializer.Serialize(sorted, writeOptions);
await File.WriteAllTextAsync(teamsFilePath, output, Encoding.UTF8);
Console.WriteLine("Saved.");

static async Task<List<TeamResult>> SearchTeam(HttpClient http, string apiName)
{
    try
    {
        var response = await http.GetAsync($"teams?search={Uri.EscapeDataString(apiName)}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("response", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray()
            .Select(e =>
            {
                var t = e.GetProperty("team");
                return new TeamResult
                {
                    Id = t.GetProperty("id").GetInt32(),
                    Name = t.GetProperty("name").GetString() ?? "",
                    Country = t.TryGetProperty("country", out var c) ? c.GetString() ?? "" : ""
                };
            })
            .ToList();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ERROR searching '{apiName}': {ex.Message}");
        Console.ResetColor();
        return [];
    }
}

static string ResolveTeamsFilePath()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null)
    {
        string path = Path.Combine(current.FullName, "src", "PlingBot", "data", "teams.json");
        if (File.Exists(path))
            return path;
        current = current.Parent;
    }
    throw new FileNotFoundException("Could not locate src/PlingBot/data/teams.json");
}

class TeamRecord
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("ApiName")] public string ApiName { get; set; } = "";
    [JsonPropertyName("Id")] public int? Id { get; set; }
}

record TeamResult(int Id, string Name, string Country)
{
    public TeamResult() : this(0, "", "") { }
}
