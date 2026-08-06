namespace PlingBot.Services;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PlingBot.Models;

// Matchar lagnamn från tipskupongen mot lagnamn från fotbolls-API:et.
// Namnen stämmer sällan exakt överens (klubbprefix, diakritiska tecken m.m.),
// därför finns både en exakt och en "fuzzy" (innehåller-baserad) matchning.
public static class TeamFixtureMatcher
{
    private static readonly string[] ClubPrefixes =
        ["FC ", "FK ", "HNK ", "MSK ", "NK ", "SK ", "ŠK ", "SC ", "AC ", "AS ", "GD ", "IF ", "BK ", "IFK ", "AIK ", "CSKA "];
    private static readonly string[] ClubSuffixes =
        [" FC", " FK", " SC", " AC", " SK", " HB", " IF", " BK", " GF", " FF", " IK", " AIF", " AO", " CF"];

    public static Match? FindMatchExact(IEnumerable<Match> matches, string homeKey, string awayKey)
    {
        return matches.FirstOrDefault(m =>
            TeamMatches(m.HomeTeam, homeKey) &&
            TeamMatches(m.AwayTeam, awayKey));
    }

    public static Match? FindMatchFuzzy(IEnumerable<Match> matches, string homeKey, string awayKey)
    {
        return matches.FirstOrDefault(m =>
            TeamMatchesFuzzy(m.HomeTeam, homeKey) &&
            TeamMatchesFuzzy(m.AwayTeam, awayKey));
    }

    public static bool TeamMatches(string apiTeam, string tipTeam)
    {
        return string.Equals(NormalizeTeamName(apiTeam), NormalizeTeamName(tipTeam), StringComparison.OrdinalIgnoreCase);
    }

    public static bool TeamMatchesFuzzy(string apiTeam, string tipTeam)
    {
        var a = NormalizeTeamName(apiTeam);
        var b = NormalizeTeamName(tipTeam);
        return a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTeamName(string value)
    {
        var s = value.Trim().Replace(".", "").Replace("-", " ");
        s = RemoveDiacritics(s);
        s = StripClubAffixes(s);
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Trim();
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string StripClubAffixes(string name)
    {
        foreach (var p in ClubPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return name[p.Length..].Trim();
        foreach (var s in ClubSuffixes)
            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                return name[..^s.Length].Trim();
        return name;
    }
}
