namespace PlingBot.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;
using static PlingBot.Services.MatchDisplayFormatter;

public class DashboardBuilder
{
    private const int MinimumMatchColumnWidth = 32;
    private const int MaximumMatchColumnWidth = 38;
    private const int PickColumnWidth = 6;

    private readonly CouponEvaluator _evaluator;

    public DashboardBuilder(CouponEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    public string Build(TipsConfig tipsConfig, string? playerMessage = null, IReadOnlyList<CouponEvent>? events = null)
    {
        IReadOnlyList<TipsMatch> tips = tipsConfig.TipsMatches;

        var sb = new StringBuilder();

        var (correct, evaluated) = _evaluator.Evaluate(tips);

        string game = tipsConfig.Data.MetaData.Game;
        string date = tipsConfig.Data.MetaData.Date;
        string player = tipsConfig.Data.MetaData.Player;

        if (game == "Europatipset") game = "VM-Tipset";

        sb.AppendLine($"{game} {date} - {player}");

        //if(!string.IsNullOrWhiteSpace(playerMessage)) sb.AppendLine(playerMessage);
        sb.AppendLine();

        int matchColumnWidth = GetMatchColumnWidth(tips, MinimumMatchColumnWidth, MaximumMatchColumnWidth);

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

        sb.AppendLine();
        sb.AppendLine($"Antal rätt: {correct}");

        if (events is { Count: > 0 })
        {
            var eventLines = events.Select(e => e.Text.Replace("**", "")).Reverse().ToList();
            int baseLen = sb.Length + "\n\nHändelser:\n".Length;
            int totalLen = eventLines.Sum(s => s.Length + 1);
            int keep = eventLines.Count;
            while (keep > 0 && baseLen + totalLen > 1900)
            {
                keep--;
                totalLen -= eventLines[keep].Length + 1;
            }
            int skipped = eventLines.Count - keep;

            sb.AppendLine();
            sb.AppendLine("Händelser:");
            foreach (var ev in eventLines.Take(keep))
                sb.AppendLine(ev);
            if (skipped > 0)
                sb.AppendLine($"(+{skipped} äldre händelser)");
        }

        return $"```{sb}```";
    }

    private static string FormatPickText(string emoji, string tip)
    {
        if (string.IsNullOrWhiteSpace(emoji))
            return $"   {tip}".PadRight(PickColumnWidth);

        return $"{emoji} {tip}".TrimEnd().PadRight(PickColumnWidth);
    }

    private static string GetPickEmoji(TipsMatch tip, string? currentSymbol)
    {
        if (tip.Match != null && tip.Match.Status.Short.Equals("PST", StringComparison.OrdinalIgnoreCase))
            return "⏳";

        return currentSymbol == null ? "" : Helpers.GetEventSymbol(tip, currentSymbol);
    }

}
