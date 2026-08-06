namespace PlingBot.Services;

using PlingBot.Config;
using PlingBot.Models;

// Sparar undan liga/plan-info för en fixture, så webbdelen kan visa den utan
// att själv behöva prata med fotbolls-API:et. Delad mellan fixture-mappningen
// och den vanliga poll-loopen, som båda stöter på nya matcher.
public static class LeagueInfoWriter
{
    public static void Store(TipsConfig tipsConfig, Match match)
    {
        if (string.IsNullOrEmpty(match.LeagueName))
            return;

        tipsConfig.Data.MetaData.LeagueMap[match.Id] = new LeagueInfo
        {
            Name = match.LeagueName,
            Flag = match.LeagueFlag,
            Logo = match.LeagueLogo,
            Round = match.LeagueRound,
            RoundSwedish = LeagueInfo.ToSwedishRound(match.LeagueRound),
            VenueName = match.VenueName,
        };
    }
}
