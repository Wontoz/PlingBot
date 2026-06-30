using DotNetEnv;

var envPath = FindFile(Directory.GetCurrentDirectory(), ".env");
if (envPath != null) Env.Load(envPath);

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

app.Run("http://localhost:5050");

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
