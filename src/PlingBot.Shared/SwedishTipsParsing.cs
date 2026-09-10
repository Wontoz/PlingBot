namespace PlingBot.Utils;

using System.Globalization;
using System.Text.RegularExpressions;

// Delad parsning av Svenska Spels svenskspråkiga kupongtext (stängningstid, procent, odds).
// Används av både TipsScraper (skrapning) och PlingBot (CouponPercentageService).
public static class SwedishTipsParsing
{
    public static int? ParsePercentage(string value)
    {
        value = value.Trim().TrimEnd('%').Trim();
        return int.TryParse(value, out int percentage) ? percentage : null;
    }

    public static decimal? ParseOdds(string value)
    {
        value = value.Trim();
        if (decimal.TryParse(value, NumberStyles.Any, new CultureInfo("sv-SE"), out decimal odds))
            return odds;
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal oddsEn))
            return oddsEn;
        return null;
    }

    public static DateTime? ParseSwedishStartTimeUtc(string value, DateTime nowLocal)
    {
        var match = Regex.Match(
            value,
            @"(?:(?<day>idag|i dag|imorgon|i morgon|måndag|tisdag|onsdag|torsdag|fredag|lördag|söndag|\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2})\s+)?(?<time>\d{1,2}:\d{2})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
            return null;

        if (!TimeOnly.TryParseExact(match.Groups["time"].Value, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return null;

        string rawDay = match.Groups["day"].Value;
        DateOnly date = ResolveSwedishDate(rawDay, nowLocal);
        var localStart = date.ToDateTime(time, DateTimeKind.Unspecified);

        if (IsSwedishWeekday(rawDay) && localStart <= nowLocal)
            localStart = localStart.AddDays(7);

        return SwedishTime.ToUtc(localStart);
    }

    private static DateOnly ResolveSwedishDate(string rawDay, DateTime nowLocal)
    {
        if (string.IsNullOrWhiteSpace(rawDay))
            return DateOnly.FromDateTime(nowLocal);

        string day = rawDay.Trim().ToLower(new CultureInfo("sv-SE"));

        if (day is "idag" or "i dag")
            return DateOnly.FromDateTime(nowLocal);

        if (day is "imorgon" or "i morgon")
            return DateOnly.FromDateTime(nowLocal.AddDays(1));

        if (DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exactDate))
            return DateOnly.FromDateTime(exactDate);

        if (DateTime.TryParseExact(day, "d/M", CultureInfo.InvariantCulture, DateTimeStyles.None, out var shortDate))
            return new DateOnly(nowLocal.Year, shortDate.Month, shortDate.Day);

        DayOfWeek? weekday = ParseSwedishWeekday(day);
        if (!weekday.HasValue)
            return DateOnly.FromDateTime(nowLocal);

        int daysForward = ((int)weekday.Value - (int)nowLocal.DayOfWeek + 7) % 7;
        return DateOnly.FromDateTime(nowLocal.AddDays(daysForward));
    }

    private static DayOfWeek? ParseSwedishWeekday(string value)
    {
        return value switch
        {
            "måndag" => DayOfWeek.Monday,
            "tisdag" => DayOfWeek.Tuesday,
            "onsdag" => DayOfWeek.Wednesday,
            "torsdag" => DayOfWeek.Thursday,
            "fredag" => DayOfWeek.Friday,
            "lördag" => DayOfWeek.Saturday,
            "söndag" => DayOfWeek.Sunday,
            _ => null
        };
    }

    private static bool IsSwedishWeekday(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string day = value.Trim().ToLower(new CultureInfo("sv-SE"));
        return ParseSwedishWeekday(day).HasValue;
    }
}
