using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PlingBot.Models;

public record class TipsMatch
{
    public int Number { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public string HomeKey { get; set; } = "";
    public string AwayKey { get; set; } = "";
    public string Tip { get; set; } = "";
    public int? FixtureId { get; set; }
    public bool IsFinished { get; set; }

    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
    public int LastHomeGoals { get; set; }
    public int LastAwayGoals { get; set; }

    public DateTime? LastUpdatedUtc { get; set; }
    public DateTime? LastRedCardCheckUtc { get; set; }
    public HashSet<string> AnnouncedEventKeys { get; set; } = new();

    [JsonIgnore]
    public Match? Match { get; set; }
}