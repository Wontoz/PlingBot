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
    public string Outcome { get; set; } = "";
    public int? FixtureId { get; set; }
    public int? HomeTeamId { get; set; }
    public int? AwayTeamId { get; set; }
    public DateTime? KickoffUtc { get; set; }
    public bool IsFinished { get; set; }

    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
    public int LastHomeGoals { get; set; }
    public int LastAwayGoals { get; set; }
    public int? Elapsed { get; set; }
    public int? Extra { get; set; }
    public string? StatusShort { get; set; }

    public int? Percentage1 { get; set; }
    public int? PercentageX { get; set; }
    public int? Percentage2 { get; set; }
    public decimal? Odds1 { get; set; }
    public decimal? OddsX { get; set; }
    public decimal? Odds2 { get; set; }

    public DateTime? LastUpdatedUtc { get; set; }
    public HashSet<string> AnnouncedEventKeys { get; set; } = new();
    public MatchStatistics? Statistics { get; set; }
    public TeamLineup? HomeLineup { get; set; }
    public TeamLineup? AwayLineup { get; set; }
    public bool BackfillComplete { get; set; }

    [JsonIgnore]
    public Match? Match { get; set; }
}
