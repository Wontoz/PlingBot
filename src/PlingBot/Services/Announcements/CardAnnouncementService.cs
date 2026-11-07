namespace PlingBot.Services;

using Discord;
using PlingBot.Models;
using PlingBot.Utils;

public class CardAnnouncementService
{
    private readonly DiscordAnnouncementService discord;

    public CardAnnouncementService(DiscordAnnouncementService discord)
    {
        this.discord = discord;
    }

    public async Task<bool> AnnounceRedCardsAsync(
        IMessageChannel channel,
        TipsMatch tip,
        Match match,
        IReadOnlyList<MatchEvent> matchEvents)
    {
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
        string comment = string.IsNullOrWhiteSpace(evt?.Comments) ? "" : $" - {evt.Comments}";
        string message = $"🟥 {symbol} Rött kort! {team} - {player} {(evt != null ? Helpers.GetMinute(evt) : Helpers.GetMinute(match))}{comment}";

        await discord.AnnounceAsync(channel, message, ConsoleColor.DarkRed, "Red card announced", couponEvent: CouponEvent.Create(
            "Card", message,
            key: key, fixtureId: match.Id, detail: evt?.Detail, teamId: evt?.TeamId, team: team,
            elapsed: evt?.Elapsed ?? match.Elapsed, extra: evt?.Extra ?? match.Extra, score: match.Score,
            playerId: evt?.PlayerId, player: evt?.Player, comments: evt?.Comments));
    }

    private static bool IsRedCardEvent(MatchEvent ev)
    {
        return string.Equals(ev.Detail, "Red Card", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ev.Detail, "Yellow Red Card", StringComparison.OrdinalIgnoreCase);
    }
}
