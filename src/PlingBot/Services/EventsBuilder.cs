namespace PlingBot.Services;

using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class EventsBuilder
{
    private const int SeparatorWidth = 80;

    private readonly TipsConfig _tipsConfig;
    private readonly FootballApiClient _api;

    public EventsBuilder(TipsConfig tipsConfig, FootballApiClient api)
    {
        _tipsConfig = tipsConfig;
        _api = api;
    }

    public async Task HandleAsync(SocketMessage message, string arg)
    {
        if (!int.TryParse(arg.Trim(), out int tipNumber))
        {
            await message.Channel.SendMessageAsync("Ange matchnummer, t.ex. `!events 1`");
            return;
        }

        var tip = _tipsConfig.TipsMatches.FirstOrDefault(t => t.Number == tipNumber);
        if (tip == null)
        {
            await message.Channel.SendMessageAsync($"Hittade ingen match #{tipNumber}.");
            return;
        }

        if (!tip.FixtureId.HasValue)
        {
            await message.Channel.SendMessageAsync($"Match #{tipNumber} ({tip.HomeTeam} vs {tip.AwayTeam}) har inget fixture-ID ännu.");
            return;
        }

        var loadingMsg = await message.Channel.SendMessageAsync("Hämtar händelser...");

        var events = await _api.FetchMatchEventsAsync(tip.FixtureId.Value);

        if (events.Count == 0)
        {
            var errorMsg = await message.Channel.SendMessageAsync($"Inga händelser tillgängliga för match #{tipNumber}.");
            Helpers.DeleteAfterDelay(TimeSpan.FromMinutes(2),loadingMsg, errorMsg);
            return;
        }

        var eventsMsg = await message.Channel.SendMessageAsync(Format(tipNumber, tip, events));
        Helpers.DeleteAfterDelay(TimeSpan.FromMinutes(2),loadingMsg, eventsMsg);
    }

    private static string Format(int tipNumber, TipsMatch tip, List<MatchEvent> events)
    {
        var ordered = events.OrderBy(Helpers.GetEventSortValue).ToList();

        string score = GetMatchScore(tip);
        string header = $"**#{tipNumber} {tip.HomeTeam} - {tip.AwayTeam}{score}**";
        int budget = 1900 - header.Length;

        int teamW = Math.Max(tip.HomeTeam.Length, tip.AwayTeam.Length);
        int homeGoals = 0, awayGoals = 0;
        string currentPeriod = "";
        var lines = new List<string>();

        foreach (var ev in ordered)
        {
            string period = GetPeriod(ev);
            if (period != currentPeriod)
            {
                lines.Add(MakeSeparator(period));
                currentPeriod = period;
            }

            bool isHome = tip.HomeTeamId.HasValue
                ? ev.TeamId == tip.HomeTeamId
                : !string.Equals(ev.Team, tip.AwayTeam, StringComparison.OrdinalIgnoreCase);
            string hb = isHome ? tip.HomeTeam : tip.AwayTeam;
            string minute = FormatMinute(ev);
            string label = GetLabel(ev);

            string scoreStr = "";
            if (IsScoringGoal(ev))
            {
                bool ownGoal = string.Equals(ev.Detail, "Own Goal", StringComparison.OrdinalIgnoreCase);
                if (ownGoal ? !isHome : isHome) homeGoals++;
                else awayGoals++;
                scoreStr = $" → {homeGoals}-{awayGoals}";
            }

            string desc = GetDescription(ev) + scoreStr;
            lines.Add($"{minute,6} {PadLabel(label, 15)}  {hb.PadRight(teamW)}      {desc}");
        }

        var sb = new StringBuilder();
        int skipped = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i] + "\n";
            if (sb.Length + line.Length > budget) { skipped = lines.Count - i; break; }
            sb.Append(line);
        }

        if (skipped > 0)
            sb.Append($"(+{skipped} händelser)");

        return $"{header}\n```\n{sb}```";
    }

    private static string PadLabel(string label, int width)
    {
        int extra = 0;
        for (int i = 0; i < label.Length; i++)
        {
            if (label[i] is >= '☀' and <= '➿')
            {
                bool hasVariationSelector = i + 1 < label.Length && label[i + 1] == '️';
                if (!hasVariationSelector)
                    extra++;
            }
        }
        return label.PadRight(width - extra);
    }

    private static string GetMatchScore(TipsMatch tip)
    {
        if (tip.Match != null)
            return $"  {tip.Match.HomeGoals}-{tip.Match.AwayGoals}";

        if (tip.HomeScore != 0 || tip.AwayScore != 0)
            return $"  {tip.HomeScore}-{tip.AwayScore}";

        return "";
    }

    private static string GetPeriod(MatchEvent ev)
    {
        if (ev.Elapsed <= 45) return "FÖRSTA HALVLEK";
        if (ev.Elapsed <= 90) return "ANDRA HALVLEK";
        if (ev.Elapsed <= 105) return "FÖRLÄNGNING 1";
        return "FÖRLÄNGNING 2";
    }

    private static string MakeSeparator(string label) =>
        $"── {label} " + new string('─', Math.Max(0, SeparatorWidth - label.Length - 4));

    private static string FormatMinute(MatchEvent ev)
    {
        if (ev.Elapsed <= 0)
            return "";

        return ev.Extra > 0
            ? $"{ev.Elapsed}+{ev.Extra}'"
            : $"{ev.Elapsed}'";
    }

    private static bool IsScoringGoal(MatchEvent ev) =>
        string.Equals(ev.Type, "Goal", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(ev.Detail, "Missed Penalty", StringComparison.OrdinalIgnoreCase);

    private static string GetLabel(MatchEvent ev) =>
        (ev.Type?.ToLowerInvariant(), ev.Detail?.ToLowerInvariant()) switch
        {
            ("goal",  "normal goal")        => "⚽MÅL",
            ("goal",  "own goal")           => "⚽SJÄLVMÅL",
            ("goal",  "penalty")            => "⚽MÅL (STRAFF)",
            ("goal",  "missed penalty")     => "🚫MISSAD STRAFF",
            ("card",  "yellow card")        => "🟨GULT KORT",
            ("card",  "second yellow card") => "🟥ANDRA GULA",
            ("card",  "red card")           => "🟥RÖTT KORT",
            ("subst", _)                    => GetSubstLabel(ev.Detail),
            ("var",   "goal cancelled")     => "⚠️VAR BORTDÖMT",
            ("var",   _)                    => "⚠️VAR",
            _                               => ev.Type ?? "?"
        };

    private static string GetSubstLabel(string? detail)
    {
        int lastSpace = detail?.LastIndexOf(' ') ?? -1;
        return lastSpace >= 0 ? $"🔄BYTE {detail![(lastSpace + 1)..]}" : "BYTE";
    }

    private static string GetDescription(MatchEvent ev) =>
        ev.Type?.ToLowerInvariant() switch
        {
            "goal"  => FormatGoalDesc(ev),
            "card"  => FormatCardDesc(ev),
            "subst" => FormatSubstDesc(ev),
            "var"   => FormatVarDesc(ev),
            _       => ev.Player ?? ""
        };

    private static string FormatGoalDesc(MatchEvent ev)
    {
        string player = ev.Player ?? "Okänd";
        if (string.Equals(ev.Detail, "Missed Penalty", StringComparison.OrdinalIgnoreCase)) return player;
        
        string assist = !string.IsNullOrWhiteSpace(ev.Assist) ? $" (assist: {ev.Assist})" : "";
        return $"{player}{assist}";
    }

    private static string FormatCardDesc(MatchEvent ev)
    {
        string player = ev.Player ?? "Okänd";
        string comment = !string.IsNullOrWhiteSpace(ev.Comments) ? $" - {ev.Comments}" : "";
        return $"{player}{comment}";
    }

    private static string FormatSubstDesc(MatchEvent ev)
    {
        string playerIn = ev.Assist ?? "?";
        string playerOut = ev.Player ?? "?";
        return $"UT: {playerOut} IN: {playerIn}";
    }

    private static string FormatVarDesc(MatchEvent ev)
    {
        string detail = ev.Detail ?? "";
        string player = !string.IsNullOrWhiteSpace(ev.Player) ? $" - {ev.Player}" : "";
        return $"{detail}{player}";
    }

}
