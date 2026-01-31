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
            var m = tip.Match;
            if (m == null) continue;

            evaluated++;
            if (tip.Tip.Contains(m.Symbol))
                correct++;
        }

        return (correct, evaluated);
    }
}