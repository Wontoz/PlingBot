namespace PlingBot.Models;
public record Match
{
    public int Id { get; init; }
    public DateTime Date { get; init; }
    public required Status Status { get; init; } = Status.Unknown;
    public required string HomeTeam { get; init; }
    public  required string AwayTeam { get; init; }
    public int? HomeTeamId { get; init; }
    public int? AwayTeamId { get; init; }
    public int HomeGoals { get; init; }
    public int AwayGoals { get; init; }
    public int Elapsed { get; init; }
    public int Extra { get; init; }
    public MatchStatistics? Statistics { get; init; }
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

public sealed record Status(string Short, string Long, string Type, string Description)
{
    public static readonly Status Unknown = new("UNK", "Unknown", "", "");
}

public record MatchStatistics
{
    public TeamStatistics Home { get; init; } = new();
    public TeamStatistics Away { get; init; } = new();
}

public record TeamStatistics
{
    public string TeamName { get; init; } = "";
    public string? ShotsOnGoal { get; init; }
    public string? ShotsOffGoal { get; init; }
    public string? TotalShots { get; init; }
    public string? BlockedShots { get; init; }
    public string? ShotsInsideBox { get; init; }
    public string? ShotsOutsideBox { get; init; }
    public string? Fouls { get; init; }
    public string? CornerKicks { get; init; }
    public string? Offsides { get; init; }
    public string? BallPossession { get; init; }
    public string? YellowCards { get; init; }
    public string? RedCards { get; init; }
    public string? GoalkeeperSaves { get; init; }
    public string? TotalPasses { get; init; }
    public string? PassesAccurate { get; init; }
    public string? PassesPercent { get; init; }
}
