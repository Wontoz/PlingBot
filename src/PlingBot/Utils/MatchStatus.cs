namespace PlingBot.Utils;

// Samlad plats för matchstatus-predikat som annars låg duplicerade i flera
// services. OBS: rör inte ShouldSkip/IsLive för ET/BT/P — förlängning är
// medvetet ignorerat i botens flöde, inte ett förbiseende.
public static class MatchStatus
{
    public static bool ShouldSkip(string status) =>
        status is "NS" or "TBD" or "ET" or "BT" or "P";

    public static bool IsFinished(string status) =>
        status.Equals("FT", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("AET", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("PEN", StringComparison.OrdinalIgnoreCase);

    public static bool IsLive(string status) =>
        status.Equals("1H", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("2H", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("LIVE", StringComparison.OrdinalIgnoreCase);
}
