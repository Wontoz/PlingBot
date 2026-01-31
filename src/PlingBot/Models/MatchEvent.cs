namespace PlingBot.Models;
public record MatchEvent
{
    public string Player { get; init; }
    public string Team { get; init; }
    public int Minute { get; init; }
    public string ExtraTime { get; init; }
}