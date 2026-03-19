namespace PlingBot.Models;
public record Match
{
    public int Id { get; init; }
    public required string Status { get; init; }
    public required string HomeTeam { get; init; }
    public  required string AwayTeam { get; init; }
    public int? HomeTeamId { get; init; }
    public int? AwayTeamId { get; init; }
    public int HomeGoals { get; init; }
    public int AwayGoals { get; init; }
    public int Elapsed { get; init; }
    public int Extra { get; init; }
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