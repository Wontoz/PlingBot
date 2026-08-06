namespace PlingBot.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PlingBot.Models;
using PlingBot.Utils;

internal static class MatchDisplayFormatter
{
    internal const int StatusColumnWidth = 5;
    internal const int ScoreColumnWidth = 8;
    internal const int StatusAndScoreColumnWidth = StatusColumnWidth + 1 + ScoreColumnWidth;

    internal static string FormatSymbolBox(string symbol, string? currentSymbol) =>
        symbol == currentSymbol ? symbol + " " : "  ";

    internal static string FormatPercentages(TipsMatch tip)
    {
        if (!tip.Percentage1.HasValue || !tip.PercentageX.HasValue || !tip.Percentage2.HasValue)
            return "";
        return $" | {tip.Percentage1}-{tip.PercentageX}-{tip.Percentage2}%";
    }

    internal static int GetMatchColumnWidth(IReadOnlyList<TipsMatch> tips, int min, int max)
    {
        int longest = tips
            .Select(tip => $"{tip.HomeTeam} - {tip.AwayTeam}".Length)
            .DefaultIfEmpty(min)
            .Max();

        return Math.Clamp(longest, min, max);
    }

    internal static string FormatMatchText(TipsMatch tip, int width)
    {
        string matchText = $"{tip.HomeTeam} - {tip.AwayTeam}";

        if (matchText.Length > width)
            matchText = matchText[..Math.Max(0, width - 3)] + "...";

        return matchText.PadRight(width);
    }

    internal static string FormatStatusAndScore(TipsMatch tip)
    {
        string status = FormatStatus(GetFixtureStatus(tip));
        string score = GetScore(tip);

        if (string.IsNullOrWhiteSpace(score))
            return status.PadLeft(StatusAndScoreColumnWidth);

        int extraStatusWidth = Math.Max(0, status.Length - StatusColumnWidth);
        int scoreWidth = Math.Max(0, ScoreColumnWidth - extraStatusWidth);
        return $"{status} {score.PadLeft(scoreWidth)}";
    }

    internal static string GetFixtureStatus(TipsMatch tip)
    {
        if (tip.Match == null)
        {
            if (tip.LastUpdatedUtc != null || tip.HomeScore != 0 || tip.AwayScore != 0)
                return "FT";

            return "";
        }

        switch (tip.Match.Status.Short.ToUpperInvariant())
        {
            case "NS":
            case "TBD":
                DateTime localDate = SwedishTime.ToLocal(tip.Match.Date.ToUniversalTime());
                int dayDiff = (localDate.Date - SwedishTime.Now().Date).Days;
                string time = localDate.ToString("HH:mm");

                if (dayDiff == 0) return $"Idag {time}";
                if (dayDiff == 1) return $"Imorgon {time}";
                if (dayDiff <= 7)
                {
                    string dayName = localDate.ToString("dddd", new CultureInfo("sv-SE"));
                    return char.ToUpper(dayName[0]) + dayName[1..] + " " + time;
                }
                return localDate.ToString("yyyy-MM-dd HH:mm");

            case "PST":  return "Uppskjuten";
            case "1H":
            case "2H":
            case "LIVE": return tip.Match.Elapsed > 0 ? FormatMatchMinute(tip.Match) : "";
            case "HT":   return "HT";
            case "SUSP":
            case "INT":
            case "ABD":  return "Avbruten";
            case "CANC": return "Inställd";
            case "AWD":  return "Tilldelad";
            case "WO":   return "WalkOver";
            case "ET":
            case "BT":
            case "P":
            default:     return "FT";
        }
    }

    internal static string GetScore(TipsMatch tip)
    {
        if (tip.Match is { } m)
        {
            if (IsPostponed(m.Status.Short) || FixtureNotStarted(m))
                return "";
            return $"{m.HomeGoals}-{m.AwayGoals}";
        }

        return tip.LastUpdatedUtc != null || tip.HomeScore != 0 || tip.AwayScore != 0
            ? $"{tip.HomeScore}-{tip.AwayScore}"
            : "-";
    }

    private static string FormatStatus(string status) =>
        FormatMinuteStatus(status) ?? status.PadLeft(StatusColumnWidth);

    private static string? FormatMinuteStatus(string status)
    {
        if (!status.EndsWith("'", StringComparison.Ordinal))
            return null;

        string minuteText = status[..^1];
        int extraSeparator = minuteText.IndexOf('+');
        string extraText = extraSeparator >= 0 ? minuteText[(extraSeparator + 1)..] : "";
        if (extraSeparator >= 0)
            minuteText = minuteText[..extraSeparator];

        if (!int.TryParse(minuteText, out int minute))
            return null;

        if (string.IsNullOrWhiteSpace(extraText))
            return $"{minute}'".PadLeft(4).PadRight(StatusColumnWidth);

        return $"{minute}+{extraText}'".PadLeft(StatusColumnWidth + 1);
    }

    private static string FormatMatchMinute(Match match)
    {
        string extra = match.Extra > 0 ? $"+{match.Extra}" : "";
        return $"{match.Elapsed}{extra}'";
    }

    private static bool IsPostponed(string status) =>
        status.Equals("PST", StringComparison.OrdinalIgnoreCase);

    private static bool FixtureNotStarted(Match match)
    {
        if (!string.IsNullOrWhiteSpace(match.Status.Type))
            return match.Status.Type.Equals("Scheduled", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(match.Status.Short))
            return match.Status.Short.Equals("NS", StringComparison.OrdinalIgnoreCase) ||
                   match.Status.Short.Equals("TBD", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(match.Status.Long))
            return match.Status.Long.Equals("Not Started", StringComparison.OrdinalIgnoreCase) ||
                   match.Status.Long.Equals("Time To Be Defined", StringComparison.OrdinalIgnoreCase);

        return true;
    }


}
