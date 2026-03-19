namespace PlingBot.Models;

public class MatchEvent
{
    public string? Type { get; set; }
    public string? Detail { get; set; }
    public string? Player { get; set; }
    public string? Team { get; set; }

    public int Elapsed { get; set; }
    public int Extra { get; set; }
}