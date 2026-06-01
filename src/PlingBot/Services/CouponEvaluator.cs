namespace PlingBot.Services;
using System.Collections.Generic;
using System.Text;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class CouponEvaluator
{
    public (int correct, int evaluated) Evaluate(IReadOnlyList<TipsMatch> tips)
    {
        int correct = 0;
        int evaluated = 0;

        foreach (var tip in tips)
        {
            string? symbol = GetCurrentSymbol(tip);
            if (symbol == null)
                continue;

            evaluated++;

            if (!string.IsNullOrWhiteSpace(tip.Tip) && tip.Tip.Contains(symbol))
                correct++;
        }

        return (correct, evaluated);
    }

    public string BuildCouponStatusMessage(TipsConfig tipsConfig, string? em = null, IReadOnlyList<string>? events = null)
    {
        IReadOnlyList<TipsMatch> tips = tipsConfig.TipsMatches;

        var sb = new StringBuilder();

        var (correct, evaluated) = Evaluate(tips);
        
        sb.AppendLine($"{tipsConfig.Data.MetaData.Game} {tipsConfig.Data.MetaData.Date} - {tipsConfig.Data.MetaData.Player}");
        sb.AppendLine();

        foreach (var tip in tips)
        {
            string? currentSymbol = GetCurrentSymbol(tip);
 
            string status = GetDisplayStatus(tip);
            string score = GetScore(tip);
            if (!string.IsNullOrWhiteSpace(score))
            {
                status += $"        {score}";
            }

            string one = FormatSymbolBox("1", currentSymbol);
            string x = FormatSymbolBox("X", currentSymbol);
            string two = FormatSymbolBox("2", currentSymbol);

            string emoji = currentSymbol == null ? "⏳" : Helpers.GetEventSymbol(tip, currentSymbol);
            string matchText = $"{tip.HomeTeam} - {tip.AwayTeam}";

            sb.AppendLine($"{tip.Number,2}. {matchText,-32} {status, 14} | {one}{x}{two} | {emoji} {tip.Tip}");
        }

        if (!string.IsNullOrWhiteSpace(em))
        {
            sb.AppendLine();
            sb.AppendLine(em);
        }

        sb.AppendLine();
        sb.AppendLine($"Antal rätt: {correct}");

        if (events != null && events.Count > 0)
    {
        var visibleEvents = events.ToList();

        while (visibleEvents.Count > 0)
        {
            var temp = new StringBuilder(sb.ToString());

            temp.AppendLine();
            temp.AppendLine("Händelser:");

            foreach (var ev in visibleEvents)
                temp.AppendLine(ev.Replace("**", ""));

            if (temp.Length <= 1900)
                break;

            visibleEvents.RemoveAt(0);
        }

        sb.AppendLine();
        sb.AppendLine("Händelser:");

        foreach (var ev in visibleEvents)
            sb.AppendLine(ev.Replace("**", ""));

        int hiddenEvents = events.Count - visibleEvents.Count;

        if (hiddenEvents > 0)
            sb.AppendLine($"(+{hiddenEvents} äldre händelser)");
    }
        return $"```{sb}```";
    }

    private static string FormatSymbolBox(string symbol, string? currentSymbol)
    {
        if (symbol == currentSymbol)
            return symbol;

        return $"  ";
    }

    private static string GetDisplayStatus(TipsMatch tip)
    {
        if (tip.Match == null)
        {
            if (tip.LastUpdatedUtc != null ||
                tip.HomeScore != 0 ||
                tip.AwayScore != 0)
            {
                return "FT";
            }

            return "";
        }

        string status = tip.Match.Status;

        if (status.Equals("Half Time", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("Halftime", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("HT", StringComparison.OrdinalIgnoreCase))
        {
            return "HT ";
        }

        if (status.Equals("First Half", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("1H", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("Second Half", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("2H", StringComparison.OrdinalIgnoreCase))
        {
            return tip.Match.Elapsed > 0
                ? $"{tip.Match.Elapsed}'"
                : "";
        }

        if (status.Equals("Not Started", StringComparison.OrdinalIgnoreCase))
        {
            
            DateTime localDate = tip.Match.Date.ToLocalTime();
            DateTime today = DateTime.Today;

            int dayDiff = (localDate.Date - today).Days;

            string time = localDate.ToString("HH:mm");

            if (dayDiff == 0)
                return $"Idag {time}";

            if (dayDiff == 1)
                return $"Imorgon {time}";

            if (dayDiff > 1 && dayDiff <= 7)
            {
                string dayName = localDate.ToString("dddd", new System.Globalization.CultureInfo("sv-SE"));
                dayName = char.ToUpper(dayName[0]) + dayName[1..];

                return $"{dayName} {time}";
            }

            return localDate.ToString("yyyy-MM-dd HH:mm");
        }

        return "FT ";
    }

    private static string GetScore(TipsMatch tip)
    {
        if (tip.Match != null && tip.Match.Status.Equals("Not Started", StringComparison.OrdinalIgnoreCase)) return "";

        if (tip.Match != null) return $"{tip.Match.HomeGoals}-{tip.Match.AwayGoals}";

        if (tip.LastUpdatedUtc != null ||
            tip.HomeScore != 0 ||
            tip.AwayScore != 0)
        {
            return $"{tip.HomeScore}-{tip.AwayScore}";
        }

        return "-";
    }

    private static string? GetCurrentSymbol(TipsMatch tip)
    {
        if (tip.Match != null)
            return tip.Match.Symbol;

        if (tip.LastUpdatedUtc == null)
            return null;

        return GetSymbolFromScores(tip.HomeScore, tip.AwayScore);
    }

    private static string GetSymbolFromScores(int homeScore, int awayScore)
    {
        if (homeScore > awayScore) return "1";
        if (homeScore < awayScore) return "2";
        return "X";
    }
}