namespace PlingBot.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class DashboardBuilder
{
    private const int MinimumMatchColumnWidth = 32;
    private const int MaximumMatchColumnWidth = 38;
    private const int PickColumnWidth = 6;
    private const int StatusColumnWidth = 5;
    private const int ScoreColumnWidth = 8;
    private const int StatusAndScoreColumnWidth = StatusColumnWidth + 1 + ScoreColumnWidth;

    private readonly CouponEvaluator _evaluator;

    public DashboardBuilder(CouponEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    public string Build(TipsConfig tipsConfig, string? extraMessage = null, IReadOnlyList<CouponEvent>? events = null)
    {
        IReadOnlyList<TipsMatch> tips = tipsConfig.TipsMatches;

        var sb = new StringBuilder();

        var (correct, evaluated) = _evaluator.Evaluate(tips);

        string game = tipsConfig.Data.MetaData.Game;
        string date = tipsConfig.Data.MetaData.Date;
        string player = tipsConfig.Data.MetaData.Player;

        if (game == "Europatipset") game = "VM-Tipset";

        sb.AppendLine($"{game} {date} - {player}");
        sb.AppendLine();

        int matchColumnWidth = GetMatchColumnWidth(tips);

        foreach (var tip in tips)
        {
            string? currentSymbol = CouponEvaluator.GetCurrentSymbol(tip);

            string statusAndScore = FormatStatusAndScore(tip);
            string one = FormatSymbolBox("1", currentSymbol);
            string x = FormatSymbolBox("X", currentSymbol);
            string two = FormatSymbolBox("2", currentSymbol);
            string emoji = GetPickEmoji(tip, currentSymbol);
            string matchText = FormatMatchText(tip, matchColumnWidth);
            string pickText = FormatPickText(emoji, tip.Tip);
            string percentages = FormatPercentages(tip);

            sb.AppendLine($"{tip.Number,2}. {matchText} {statusAndScore} | {one}{x}{two} | {pickText}{percentages}");
        }

        if (!string.IsNullOrWhiteSpace(extraMessage))
        {
            sb.AppendLine();
            sb.AppendLine(extraMessage);
        }

        sb.AppendLine();
        sb.AppendLine($"Antal rätt: {correct}");

        if (events is { Count: > 0 })
        {
            var eventLines = events.Select(e => e.Text.Replace("**", "")).ToList();
            int baseLen = sb.Length + "\n\nHändelser:\n".Length;
            int totalLen = eventLines.Sum(s => s.Length + 1);
            int skip = 0;
            while (skip < eventLines.Count && baseLen + totalLen > 1900)
            {
                totalLen -= eventLines[skip].Length + 1;
                skip++;
            }

            sb.AppendLine();
            sb.AppendLine("Händelser:");
            foreach (var ev in eventLines.Skip(skip))
                sb.AppendLine(ev);
            if (skip > 0)
                sb.AppendLine($"(+{skip} äldre händelser)");
        }

        return $"```{sb}```";
    }

    private static string FormatSymbolBox(string symbol, string? currentSymbol) =>
        symbol == currentSymbol ? symbol.PadRight(2) : "  ";

    private static int GetMatchColumnWidth(IReadOnlyList<TipsMatch> tips)
    {
        int longest = tips
            .Select(tip => $"{tip.HomeTeam} - {tip.AwayTeam}".Length)
            .DefaultIfEmpty(MinimumMatchColumnWidth)
            .Max();

        return Math.Clamp(longest, MinimumMatchColumnWidth, MaximumMatchColumnWidth);
    }

    private static string FormatMatchText(TipsMatch tip, int width)
    {
        string matchText = $"{tip.HomeTeam} - {tip.AwayTeam}";

        if (matchText.Length > width)
            matchText = matchText[..Math.Max(0, width - 3)] + "...";

        return matchText.PadRight(width);
    }

    private static string FormatPickText(string emoji, string tip)
    {
        if (string.IsNullOrWhiteSpace(emoji))
            return $"   {tip}".PadRight(PickColumnWidth);

        return $"{emoji} {tip}".TrimEnd().PadRight(PickColumnWidth);
    }

    private static string GetPickEmoji(TipsMatch tip, string? currentSymbol)
    {
        if (tip.Match != null && IsPostPoned(tip.Match.Status.Short))
            return "⏳";

        return currentSymbol == null ? "" : Helpers.GetEventSymbol(tip, currentSymbol);
    }

    private static string FormatStatusAndScore(TipsMatch tip)
    {
        string status = FormatStatus(GetFixtureStatus(tip));
        string score = GetScore(tip);

        if (string.IsNullOrWhiteSpace(score))
            return status.PadLeft(StatusAndScoreColumnWidth);

        int extraStatusWidth = Math.Max(0, status.Length - StatusColumnWidth);
        int scoreWidth = Math.Max(0, ScoreColumnWidth - extraStatusWidth);

        return $"{status} {score.PadLeft(scoreWidth)}";
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
            return $"{minute}'".PadLeft(StatusColumnWidth - 1).PadRight(StatusColumnWidth);

        return $"{minute}+{extraText}'".PadLeft(StatusColumnWidth + 1);
    }

    private static string FormatPercentages(TipsMatch tip)
    {
        if (!tip.Percentage1.HasValue || !tip.PercentageX.HasValue || !tip.Percentage2.HasValue)
            return "";

        return $" | {tip.Percentage1}-{tip.PercentageX}-{tip.Percentage2}%";
    }

    private static string GetFixtureStatus(TipsMatch tip)
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
                DateTime localDate = tip.Match.Date.ToLocalTime();
                int dayDiff = (localDate.Date - DateTime.Today).Days;
                string time = localDate.ToString("HH:mm");

                if (dayDiff == 0) return $"Idag {time}";
                if (dayDiff == 1) return $"Imorgon {time}";
                if (dayDiff <= 7)
                {
                    string dayName = localDate.ToString("dddd", new CultureInfo("sv-SE"));
                    return char.ToUpper(dayName[0]) + dayName[1..] + " " + time;
                }
                return localDate.ToString("yyyy-MM-dd HH:mm");

            case "PST":      return "Uppskjuten";
            case "1H":
            case "2H":
            case "LIVE":     return tip.Match.Elapsed > 0 ? FormatMatchMinute(tip.Match) : "";
            case "HT":       return "    HT";
            case "SUSP":
            case "INT":
            case "ABD":      return "Avbruten";
            case "CANC":     return "Inställd";
            case "AWD":      return "Tilldelad";
            case "WO":       return "WalkOver";
            // Tipset räknar bara matcher till Full Tid så förlängningar ska inte trackas
            case "ET":
            case "BT":
            case "P":
            default:         return "    FT";
        }
    }

    private static string GetScore(TipsMatch tip)
    {
        if (tip.Match is { } m)
        {
            if (IsPostPoned(m.Status.Short) || FixtureNotStarted(m))
                return "";
            return $"{m.HomeGoals}-{m.AwayGoals}";
        }

        return tip.LastUpdatedUtc != null || tip.HomeScore != 0 || tip.AwayScore != 0
            ? $"{tip.HomeScore}-{tip.AwayScore}"
            : "-";
    }

    private static string FormatMatchMinute(Match match)
    {
        string extra = match.Extra > 0 ? $"+{match.Extra}" : "";
        return $"{match.Elapsed}{extra}'";
    }

    private static bool IsPostPoned(string status) =>
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
