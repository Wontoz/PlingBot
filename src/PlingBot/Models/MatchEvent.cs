namespace PlingBot.Models;

public class MatchEvent
{
    public int FixtureId { get; set; }
    public string? Type { get; set; }
    public string? Detail { get; set; }
    public int? PlayerId { get; set; }
    public string? Player { get; set; }
    public int? AssistId { get; set; }
    public string? Assist { get; set; }
    public string? Team { get; set; }
    public int? TeamId { get; set; }
    public int Elapsed { get; set; }
    public int Extra { get; set; }
    public string? Comments { get; set; }
}
