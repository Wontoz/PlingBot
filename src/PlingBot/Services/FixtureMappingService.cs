namespace PlingBot.Services;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

// Mappar varje tips-rad till dess fixture-ID i fotbolls-API:et. Körs vid uppstart:
// hämtar matcher dag för dag (upp till en vecka fram) och försöker hitta rätt fixture
// för varje otippad match, med fallback mot lagnamn sparade i TeamRepository.
public class FixtureMappingService
{
    private const int FixtureLookupDaysForward = 7;
    private static readonly TimeSpan FixtureDateCacheTtl = TimeSpan.FromMinutes(5);

    private readonly FootballApiClient _api;
    private readonly TipsConfig tipsConfig;
    private readonly TeamRepository teamRepo;
    private readonly Logger _logger;
    private readonly Dictionary<DateTime, (DateTime FetchedUtc, List<Match> Matches)> fixtureDateCache = new();

    public FixtureMappingService(FootballApiClient api, TipsConfig tipsConfig, TeamRepository teamRepo, Logger logger)
    {
        _api = api;
        this.tipsConfig = tipsConfig;
        this.teamRepo = teamRepo;
        _logger = logger;
    }

    public async Task InitializeFixtureIdsAsync()
    {
        var unresolvedTips = tipsConfig.TipsMatches.ToList();
        var allFetchedMatches = new List<Match>();
        int mapped = 0;
        int loaded = 0;

        _logger.Log($"Mapping {tipsConfig.TipsMatches.Count} tips...", ConsoleColor.Blue);

        for (int i = 0; i <= FixtureLookupDaysForward && unresolvedTips.Count > 0; i++)
        {
            var date = DateTime.UtcNow.Date.AddDays(i);
            var matchesForDate = await FetchMatchesByDateCachedAsync(date, forceRefresh: true);
            allFetchedMatches.AddRange(matchesForDate);

            foreach (var tip in unresolvedTips.ToList())
            {
                bool alreadyMapped = tip.FixtureId.HasValue;
                bool wasFuzzy = false;
                Match? match = alreadyMapped
                    ? matchesForDate.FirstOrDefault(m => m.Id == tip.FixtureId!.Value)
                    : ResolveNewMatch(tip, matchesForDate, out wasFuzzy);

                if (match == null)
                    continue;

                unresolvedTips.Remove(tip);
                tip.FixtureId = match.Id;
                tip.HomeTeamId ??= match.HomeTeamId;
                tip.AwayTeamId ??= match.AwayTeamId;
                tip.KickoffUtc = match.Date.ToUniversalTime();
                tip.Match = match;
                LeagueInfoWriter.Store(tipsConfig, match);

                teamRepo.Upsert(tip.HomeTeam, match.HomeTeam, match.HomeTeamId);
                teamRepo.Upsert(tip.AwayTeam, match.AwayTeam, match.AwayTeamId);

                if (alreadyMapped)
                {
                    loaded++;
                }
                else
                {
                    string fuzzyTag = wasFuzzy ? " [fuzzy]" : "";
                    _logger.Log($"Mapped tip #{tip.Number,-2} -> fixture {match.Id} ({match.HomeTeam} vs {match.AwayTeam}) {match.Date:yyyy-MM-dd HH:mm}{fuzzyTag}", ConsoleColor.Green);
                    mapped++;
                }
            }
        }

        foreach (var tip in unresolvedTips.ToList())
        {
            if (tip.FixtureId.HasValue)
            {
                var match = await _api.FetchFixtureByIdAsync(tip.FixtureId.Value);
                if (match != null)
                {
                    unresolvedTips.Remove(tip);
                    tip.HomeTeamId ??= match.HomeTeamId;
                    tip.AwayTeamId ??= match.AwayTeamId;
                    tip.KickoffUtc = match.Date.ToUniversalTime();
                    tip.Match = match;
                    LeagueInfoWriter.Store(tipsConfig, match);
                    continue;
                }
            }

            _logger.Log($"Failed to map tip #{tip.Number,-2} ({tip.HomeKey} vs {tip.AwayKey})", ConsoleColor.DarkRed);
            var candidates = allFetchedMatches
                .Where(m => TeamFixtureMatcher.TeamMatchesFuzzy(m.HomeTeam, tip.HomeKey) || TeamFixtureMatcher.TeamMatchesFuzzy(m.AwayTeam, tip.AwayKey)
                         || TeamFixtureMatcher.TeamMatchesFuzzy(m.HomeTeam, tip.AwayKey) || TeamFixtureMatcher.TeamMatchesFuzzy(m.AwayTeam, tip.HomeKey))
                .Take(3);
            foreach (var c in candidates)
                _logger.Log($"  Kandidat: {c.HomeTeam} vs {c.AwayTeam} (fixture {c.Id})", ConsoleColor.Yellow);
        }

        tipsConfig.SaveToJson();
        _logger.Log($"Mapping complete: {mapped} mapped, {loaded} loaded, {unresolvedTips.Count} failed", ConsoleColor.Cyan);
    }

    // Löser fixture-ID för en otippad match: prova först exakt/fuzzy mot kupongens
    // egna lagnycklar, och bara om inget hittas – prova mot lagnamnen som
    // TeamRepository känner till sedan tidigare kuponger.
    private Match? ResolveNewMatch(TipsMatch tip, List<Match> matchesForDate, out bool wasFuzzy)
    {
        var match = TryResolveByKeys(matchesForDate, tip.HomeKey, tip.AwayKey, out wasFuzzy);
        if (match != null)
            return match;

        var homeApi = teamRepo.FindByName(tip.HomeTeam)?.ApiName;
        var awayApi = teamRepo.FindByName(tip.AwayTeam)?.ApiName;
        if (homeApi == null || awayApi == null)
            return null;

        return TryResolveByKeys(matchesForDate, homeApi, awayApi, out wasFuzzy);
    }

    private static Match? TryResolveByKeys(List<Match> matches, string home, string away, out bool wasFuzzy)
    {
        var exact = TeamFixtureMatcher.FindMatchExact(matches, home, away);
        if (exact != null)
        {
            wasFuzzy = false;
            return exact;
        }

        var fuzzy = TeamFixtureMatcher.FindMatchFuzzy(matches, home, away);
        wasFuzzy = fuzzy != null;
        return fuzzy;
    }

    private async Task<List<Match>> FetchMatchesByDateCachedAsync(DateTime date, bool forceRefresh = false)
    {
        date = date.Date;

        if (!forceRefresh &&
            fixtureDateCache.TryGetValue(date, out var cached) &&
            DateTime.UtcNow - cached.FetchedUtc < FixtureDateCacheTtl)
        {
            return cached.Matches;
        }

        var matches = await _api.FetchMatchesByDateAsync(date);
        fixtureDateCache[date] = (DateTime.UtcNow, matches);
        return matches;
    }
}
