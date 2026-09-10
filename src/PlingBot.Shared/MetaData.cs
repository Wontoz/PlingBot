namespace PlingBot.Config;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

// Delat JSON-schema för kupongens metadata. Skrivs av TipsScraper och läses av PlingBot -
// måste vara samma typ i båda projekten så att fälten inte kan drifta isär tyst.
public class MetaData
{
    public string Player { get; set; } = "";
    public string Date { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public string Game { get; set; } = "";
    public int TotalCorrect { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? DataLastUpdatedUtc { get; set; }
    public DateTime? LastPayoutScrapedUtc { get; set; }
    public List<PayoutRow> Payouts { get; set; } = [];
    public Dictionary<int, LeagueInfo> LeagueMap { get; set; } = [];
}

public class PayoutRow
{
    public string Correct { get; set; } = "";
    public string Amount { get; set; } = "";
    public string Rows { get; set; } = "";
}

public class LeagueInfo
{
    public string Name { get; set; } = "";
    public string? Flag { get; set; }
    public string? Logo { get; set; }
    public string? Round { get; set; }
    public string? RoundSwedish { get; set; }
    public string? VenueName { get; set; }

    public static string? ToSwedishRound(string? round)
    {
        if (round == null) return null;
        var s = Regex.Replace(round, @"^Regular Season - (\d+)$", "Omgång $1");
        s = Regex.Replace(s, @" - (\d+)$", " - Omgång $1");
        s = Regex.Replace(s, @"\bRound of 32\b", "Sextondelsfinal", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bQuarter\s*-?\s*Finals?\b", "Kvartsfinal", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bQuater\s*-?\s*Finals?\b", "Kvartsfinal", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bSemi\s*-?\s*Finals?\b", "Semifinal", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bLeague Stage\b", "Ligafas", RegexOptions.IgnoreCase);
        return s;
    }
}
