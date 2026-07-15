using System.Text.Json;
using System.Text.Json.Nodes;
using DotNetEnv;

var envPath = FindFile(Directory.GetCurrentDirectory(), ".env");
if (envPath != null) Env.Load(envPath);

var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "";

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers["Pragma"] = "no-cache";
        ctx.Context.Response.Headers["Expires"] = "0";
    }
});

app.MapGet("/api/coupon", () =>
{
    var jsonDir = ResolveJsonDirectory();
    var path = FindLatestCouponJson(jsonDir);

    if (path == null)
        return Results.NotFound("Ingen kupong hittad.");

    return Results.Content(File.ReadAllText(path), "application/json");
});

app.MapGet("/api/admin/coupon", (HttpContext ctx) =>
{
    if (!IsAuthorized(ctx, adminPassword))
        return Results.Unauthorized();

    var jsonDir = ResolveJsonDirectory();
    var path = FindLatestCouponJson(jsonDir);

    if (path == null)
        return Results.NotFound("Ingen kupong hittad.");

    return Results.Content(File.ReadAllText(path), "application/json");
});

app.MapPost("/api/admin/save", async (HttpContext ctx) =>
{
    if (!IsAuthorized(ctx, adminPassword))
        return Results.Unauthorized();

    var body = await ctx.Request.ReadFromJsonAsync<AdminSaveBody>();
    if (body == null)
        return Results.BadRequest("Ogiltig body.");

    var jsonDir = ResolveJsonDirectory();
    var path = FindLatestCouponJson(jsonDir);
    if (path == null)
        return Results.NotFound("Ingen kupong hittad.");

    var json = await File.ReadAllTextAsync(path);
    var node = JsonNode.Parse(json);
    var tipsData = node?["TipsData"]?.AsArray();
    if (tipsData == null)
        return Results.Problem("Ogiltig JSON-struktur.");

    foreach (var match in tipsData)
    {
        var number = match?["Number"]?.GetValue<int>().ToString();
        if (number == null) continue;

        if (body.Tips != null && body.Tips.TryGetValue(number, out var tip))
            match!["Tip"] = tip;

        if (body.FixtureIds != null && body.FixtureIds.TryGetValue(number, out var fixtureId))
            match!["FixtureId"] = fixtureId.HasValue ? JsonValue.Create(fixtureId.Value) : null;
    }

    await File.WriteAllTextAsync(path, node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return Results.Ok();
});

app.Run("http://localhost:5050");

static bool IsAuthorized(HttpContext ctx, string password)
{
    if (string.IsNullOrEmpty(password)) return false;
    var provided = ctx.Request.Headers["X-Admin-Password"].FirstOrDefault() ?? "";
    return string.Equals(provided, password, StringComparison.Ordinal);
}

static string ResolveJsonDirectory()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null)
    {
        var candidate = Path.Combine(current.FullName, "src", "PlingBot", "json");
        if (Directory.Exists(Path.Combine(current.FullName, "src", "PlingBot")))
            return candidate;
        current = current.Parent;
    }
    return Path.Combine(Directory.GetCurrentDirectory(), "src", "PlingBot", "json");
}

static string? FindLatestCouponJson(string jsonDir)
{
    if (!Directory.Exists(jsonDir)) return null;
    return Directory.GetFiles(jsonDir, "*.json")
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
}

static string? FindFile(string startPath, string fileName)
{
    var dir = new DirectoryInfo(startPath);
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, fileName);
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

record AdminSaveBody(Dictionary<string, string>? Tips, Dictionary<string, int?>? FixtureIds);
