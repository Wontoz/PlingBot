namespace PlingBot.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PlingBot.Config;
using PlingBot.Models;
using static PlingBot.Services.MatchDisplayFormatter;

public class VersusDashboardBuilder
{
    private const int MinimumMatchColumnWidth = 28;
    private const int MaximumMatchColumnWidth = 34;

    public string Build(TipsConfig primaryConfig, VersusConfig versusConfig, IReadOnlyList<CouponEvent>? events = null)
    {
        IReadOnlyList<TipsMatch> tips = primaryConfig.TipsMatches;

        string game = primaryConfig.Data.MetaData.Game;
        if (game == "Europatipset") game = "VM-Tipset";

        string date = primaryConfig.Data.MetaData.Date;
        string primaryPlayer = primaryConfig.Data.MetaData.Player;

        var allPlayers = BuildPlayerList(primaryPlayer, primaryConfig, versusConfig);

        var sb = new StringBuilder();
        sb.AppendLine($"{game} {date} | Grand PIX!");

        int matchColumnWidth = GetMatchColumnWidth(tips, MinimumMatchColumnWidth, MaximumMatchColumnWidth);
        int nameAreaWidth = Math.Max(allPlayers.Max(p => p.Name.Length), 5);

        // Header row: spaces until the pick columns, then each player's name above their column.
        // Prefix = "{nr,2}. " (4) + match + " " (1) + statusAndScore (14) + " | " (3) + symbols + " " (1)
        int symbolWidth = FormatSymbolBox("1", null).Length;
        int prefixWidth = 4 + matchColumnWidth + 1 + StatusAndScoreColumnWidth + 3 + symbolWidth * 3 + 1;
        string playerHeader = new string(' ', prefixWidth)
            +  "  William    Jonas       Fredrik";
            
        //string.Concat(allPlayers.Select(p => FormatNameSlot(p.Name, nameAreaWidth)));
        sb.AppendLine(playerHeader);

        foreach (var tip in tips)
        {
            string? currentSymbol = CouponEvaluator.GetCurrentSymbol(tip);
            bool isPostponed = tip.Match != null &&
                tip.Match.Status.Short.Equals("PST", StringComparison.OrdinalIgnoreCase);

            string statusAndScore = FormatStatusAndScore(tip);
            string one = FormatSymbolBox("1", currentSymbol);
            string x = FormatSymbolBox("X", currentSymbol);
            string two = FormatSymbolBox("2", currentSymbol);
            string matchText = FormatMatchText(tip, matchColumnWidth);

            string picks = string.Concat(allPlayers.Select(p =>
                FormatPickSlot(p.GetTip(tip.Number), currentSymbol, isPostponed, nameAreaWidth)));
            string percentages = FormatPercentages(tip);
            string rowTail = string.IsNullOrEmpty(percentages) ? "|" : percentages.TrimStart();

            sb.AppendLine($"{tip.Number,2}. {matchText} {statusAndScore} | {one}{x}{two} {picks}{rowTail}");
        }

        sb.AppendLine();
        sb.AppendLine(BuildScoreLine(allPlayers, tips));

        if (events is { Count: > 0 })
        {
            var eventLines = events
                .Where(e => e.Type is "Goal" or "CancelledGoal" ||
                            (e.Type == "Card" && !string.Equals(e.Detail, "Yellow Card", StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Text.Replace("**", "")).Reverse().ToList();
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

    private static List<VersusPlayerView> BuildPlayerList(
        string primaryName,
        TipsConfig primaryConfig,
        VersusConfig versusConfig)
    {
        var list = new List<VersusPlayerView>();

        char primaryInitial = GetInitial(primaryName, list);
        list.Add(new VersusPlayerView(
            primaryName,
            primaryInitial,
            matchNumber => primaryConfig.TipsMatches
                .FirstOrDefault(t => t.Number == matchNumber)?.Tip));

        foreach (var p in versusConfig.Players)
        {
            char initial = GetInitial(p.Name, list);
            list.Add(new VersusPlayerView(p.Name, initial, p.GetTip));
        }

        return list;
    }

    private static char GetInitial(string name, IReadOnlyList<VersusPlayerView> existing)
    {
        if (string.IsNullOrWhiteSpace(name)) return '?';

        char first = char.ToUpper(name[0]);
        if (existing.All(p => p.Initial != first))
            return first;

        // Fallback: use a digit to distinguish (unlikely with W/J/F)
        return (char)('0' + existing.Count);
    }

    // Slot width = nameAreaWidth + 3 chars ("|" + " " + name/content + " ").
    // nameAreaWidth = max(longest player name, 5) — scales dynamically so pipes always align.
    // Both emoji/no-emoji branches produce identical string lengths:
    //   emoji:    "| {e} {tip padded} "
    //   no emoji: "|   {tip padded} "
    private static string FormatPickSlot(string? tip, string? currentSymbol, bool isPostponed, int nameAreaWidth)
    {
        string tipText = string.IsNullOrWhiteSpace(tip) ? "-" : tip;
        string emoji = string.IsNullOrWhiteSpace(tip) ? "" : GetPickEmoji(tip, currentSymbol, isPostponed);
        int tipPad = nameAreaWidth - 2;

        return string.IsNullOrEmpty(emoji)
            ? "|   " + tipText.PadRight(tipPad) + " "
            : "| " + emoji + " " + tipText.PadRight(tipPad) + " ";
    }

    private static string GetPickEmoji(string playerTip, string? currentSymbol, bool isPostponed)
    {
        if (isPostponed) return "⏳";
        if (currentSymbol == null) return "";
        return playerTip.Contains(currentSymbol) ? "✅" : "❌";
    }

    private static string BuildScoreLine(IReadOnlyList<VersusPlayerView> players, IReadOnlyList<TipsMatch> tips)
    {
        var parts = players.Select(p =>
        {
            int correct = tips.Count(t =>
            {
                string? sym = CouponEvaluator.GetCurrentSymbol(t);
                string? playerTip = p.GetTip(t.Number);
                return sym != null && !string.IsNullOrWhiteSpace(playerTip) && playerTip.Contains(sym);
            });
            return $"{p.Name}: {correct}";
        });

        return "Antal rätt: " + string.Join(" | ", parts);
    }

    private sealed class VersusPlayerView
    {
        public string Name { get; }
        public char Initial { get; }
        public Func<int, string?> GetTip { get; }

        public VersusPlayerView(string name, char initial, Func<int, string?> getTip)
        {
            Name = name;
            Initial = initial;
            GetTip = getTip;
        }
    }
}
