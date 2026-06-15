namespace PlingBot.Services;

using Discord;
using PlingBot.Models;
using PlingBot.Utils;

public class CardAnnouncementService
{
    private static readonly TimeSpan EventCheckInterval = TimeSpan.FromMinutes(2);

    private readonly DiscordAnnouncementService _discord;
    private readonly Dictionary<int, DateTime> _lastRedCardChecks = new();

    public CardAnnouncementService(DiscordAnnouncementService discord)
    {
        _discord = discord;
    }

    public async Task<bool> AnnounceRedCardsAsync(
        IMessageChannel channel,
        TipsMatch tip,
        Match match,
        IReadOnlyList<MatchEvent> matchEvents,
        bool forceCheck = false)
    {
        bool shouldCheck = !_lastRedCardChecks.TryGetValue(match.Id, out var lastCheck) ||
            DateTime.UtcNow - lastCheck >= EventCheckInterval;
        if (!shouldCheck && !forceCheck)
            return false;

        _lastRedCardChecks[match.Id] = DateTime.UtcNow;

        bool announced = false;

        var redCardEvents = matchEvents
            .Where(IsRedCardEvent)
            .OrderBy(Helpers.GetEventSortValue)
            .ToList();

        foreach (var (ev, index) in redCardEvents.Select((ev, i) => (ev, i)))
        {
            string key = AnnouncementEventKeys.BuildCardKey(match.Id, index);

            if (tip.AnnouncedEventKeys.Contains(key))
                continue;

            bool isHome = AnnouncementEventKeys.IsHomeEvent(match, ev);
            await AnnounceRedCardAsync(channel, tip, match, isHome, ev, key);
            tip.AnnouncedEventKeys.Add(key);
            announced = true;
        }

        return announced;
    }

    private async Task AnnounceRedCardAsync(IMessageChannel channel, TipsMatch tip, Match match, bool isHome, MatchEvent? evt, string key)
    {
        string team = isHome ? tip.HomeTeam : tip.AwayTeam;
        string symbol = isHome
            ? Helpers.GetEventSymbol(tip, match.Symbol, match.HomeTeam, isHomeEvent: true, isBadEvent: true)
            : Helpers.GetEventSymbol(tip, match.Symbol, match.AwayTeam, isHomeEvent: false, isBadEvent: true);

        string player = string.IsNullOrEmpty(evt?.Player) ? "Okänd spelare" : evt.Player;
        string message = $"🟥 {symbol} Rött kort! {team} - {player} {(evt != null ? Helpers.GetMinute(evt) : Helpers.GetMinute(match))}";

        await _discord.AnnounceAsync(channel, message, ConsoleColor.DarkRed, "Red card announced", couponEvent: new CouponEvent
        {
            Key = key,
            Type = "Card",
            FixtureId = match.Id,
            Detail = evt?.Detail,
            TeamId = evt?.TeamId,
            Team = team,
            Elapsed = evt?.Elapsed ?? match.Elapsed,
            Extra = evt?.Extra ?? match.Extra,
            Score = match.Score,
            Text = message,
            PlayerId = evt?.PlayerId,
            Player = evt?.Player,
            CreatedUtc = DateTime.UtcNow
        });
    }

    private static bool IsRedCardEvent(MatchEvent ev)
    {
        return string.Equals(ev.Detail, "Red Card", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ev.Detail, "Second Yellow Card", StringComparison.OrdinalIgnoreCase);
    }
}
