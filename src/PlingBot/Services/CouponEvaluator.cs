namespace PlingBot.Services;
using System.Collections.Generic;
using PlingBot.Models;

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

    private static string? GetCurrentSymbol(TipsMatch tip)
    {
        if (tip.Match != null)
            return tip.Match.Symbol;

        return GetSymbolFromScores(tip.HomeScore, tip.AwayScore);
    }

    private static string GetSymbolFromScores(int homeScore, int awayScore)
    {
        if (homeScore > awayScore) return "1";
        if (homeScore < awayScore) return "2";
        return "X";
    }
}