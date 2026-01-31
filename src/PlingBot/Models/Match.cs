namespace PlingBot.Models;
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