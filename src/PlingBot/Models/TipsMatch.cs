using System.Text.Json.Serialization;

namespace PlingBot.Models;
public record class TipsMatch
{
    public int Number { get; set; }
    public string HomeTeam { get; set; }
    public string AwayTeam { get; set; }
    public string HomeKey { get; set; }
    public string AwayKey { get; set; }
    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
    public string Tip { get; set; }
    public int? FixtureId { get; set; }
    public int? LastHomeGoals { get; set; }
    public int? LastAwayGoals { get; set; }
    [JsonIgnore] public Match? Match { get; set; } 
}


