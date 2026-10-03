using System.Globalization;
using System.Text.RegularExpressions;

namespace Huishoudplanner.Domain.Calendar;

/// <summary>An ISO 8601 week: the week-based year and the week number 1..53.</summary>
public readonly record struct IsoWeekNumber(int Year, int Week);

/// <summary>
/// All calendar conversion goes through this class (port of <c>packages/shared/src/time.ts</c>).
/// A day key is a calendar <see cref="DateOnly"/> in the household timezone. Weekdays use 0=Sunday..6=Saturday
/// unless a name says Monday-first. Time never comes from the machine clock: callers pass an instant or a
/// <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// Error mapping of the TypeScript <c>RangeError</c>: an unparsable day key is a <see cref="FormatException"/>
/// (<see cref="Parse"/>); an unknown timezone, an invalid time of day and every other range violation is an
/// <see cref="ArgumentOutOfRangeException"/>.
/// </remarks>
public static partial class DayKeys
{
    /// <summary>The household timezone (IANA id).</summary>
    public const string AppTimezone = "Europe/Amsterdam";

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}z")]
    private static partial Regex DayKeyPattern();

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]z")]
    private static partial Regex TimeOfDayPattern();

    [GeneratedRegex("^([0-9]{4})-W([0-9]{2})z")]
    private static partial Regex IsoWeekPattern();

    /// <summary>Resolves an IANA timezone id; an unknown id throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    public static TimeZoneInfo FindZone(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new ArgumentOutOfRangeException(nameof(timezoneId), timezoneId, "Unknown timezone.");
        }
    }

    /// <summary>Parses a 'YYYY-MM-DD' day key; anything else throws <see cref="FormatException"/>.</summary>
    public static DateOnly Parse(string key) =>
        TryParse(key, out var day) ? day : throw new FormatException($"Invalid day key: {key}");

    /// <summary>TS <c>isDayKey</c>: strict 'YYYY-MM-DD' that is a real calendar date.</summary>
    public static bool IsDayKey(string value) => TryParse(value, out _);

    /// <summary>Day key of the instant given by the clock, in the timezone (TS <c>today</c>).</summary>
    public static DateOnly Today(TimeZoneInfo tz, TimeProvider clock) => ToDayKey(clock.GetUtcNow(), tz);

    /// <summary>Day key of the given instant, in the timezone (TS <c>today</c> with an explicit <c>now</c>).</summary>
    public static DateOnly Today(TimeZoneInfo tz, DateTimeOffset now) => ToDayKey(now, tz);

    /// <summary>Calendar date of an instant in the timezone (TS <c>toDayKey</c>).</summary>
    public static DateOnly ToDayKey(DateTimeOffset instant, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, tz).DateTime);

    /// <summary>Instant of local midnight (00:00 in tz) of the day key (TS <c>fromDayKey</c>).</summary>
    public static DateTimeOffset FromDayKey(DateOnly key, TimeZoneInfo tz) => FromLocal(key.ToDateTime(TimeOnly.MinValue), tz);

    /// <summary>TS <c>isTimeOfDay</c>: strict 'HH:mm'.</summary>
    public static bool IsTimeOfDay(string value) => TimeOfDayPattern().IsMatch(value);

    /// <summary>
    /// Instant of a wall-clock time ('HH:mm') on a day key in the timezone (TS <c>fromDayKeyTime</c>). A time that
    /// the day skips (spring-forward gap) lands the same distance after the gap; an ambiguous time (fall-back
    /// overlap) means its first occurrence.
    /// </summary>
    public static DateTimeOffset FromDayKeyTime(DateOnly key, string time, TimeZoneInfo tz)
    {
        if (!IsTimeOfDay(time))
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, "Invalid time of day.");
        }

        var local = key.ToDateTime(TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture));
        return FromLocal(local, tz);
    }

    /// <summary>Calendar arithmetic on day keys; independent of DST (TS <c>addDays</c>).</summary>
    public static DateOnly AddDays(DateOnly key, int days) => key.AddDays(days);

    /// <summary>Whole calendar days from <paramref name="from"/> to <paramref name="to"/> (TS <c>daysBetween</c>).</summary>
    public static int DaysBetween(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber;

    /// <summary>0=Sunday..6=Saturday.</summary>
    public static int WeekdaySun0(DateOnly key) => (int)key.DayOfWeek;

    /// <summary>0=Monday..6=Sunday.</summary>
    public static int WeekdayMon0(DateOnly key) => Sun0ToMon0(WeekdaySun0(key));

    public static int Sun0ToMon0(int weekday) => (weekday + 6) % 7;

    public static int Mon0ToSun0(int weekday) => (weekday + 1) % 7;

    public static bool IsMonday(DateOnly key) => key.DayOfWeek == DayOfWeek.Monday;

    /// <summary>TS <c>isMonday</c> on text: false for anything that is not a valid day key.</summary>
    public static bool IsMonday(string key) => TryParse(key, out var day) && IsMonday(day);

    public static bool IsWeekend(DateOnly key) => key.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public static IsoWeekNumber IsoWeek(DateOnly key)
    {
        var date = key.ToDateTime(TimeOnly.MinValue);
        return new IsoWeekNumber(ISOWeek.GetYear(date), ISOWeek.GetWeekOfYear(date));
    }

    /// <summary>e.g. '2026-W38'.</summary>
    public static string IsoWeekLabel(DateOnly key)
    {
        var (year, week) = IsoWeek(key);
        return string.Create(CultureInfo.InvariantCulture, $"{year:0000}-W{week:00}");
    }

    /// <summary>Monday of an ISO week label, or null if the label is invalid.</summary>
    public static DateOnly? MondayOfIsoWeek(string label)
    {
        var match = IsoWeekPattern().Match(label);
        if (!match.Success)
        {
            return null;
        }

        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var week = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (year < 1 || week < 1 || week > 53)
        {
            return null;
        }

        DateOnly monday;
        try
        {
            monday = DateOnly.FromDateTime(ISOWeek.ToDateTime(year, week, DayOfWeek.Monday));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }


        return IsoWeekLabel(monday) == label ? monday : null;
    }

    /// <summary>Monday of the ISO week containing the day.</summary>
    public static DateOnly MondayOf(DateOnly key) => AddDays(key, -WeekdayMon0(key));

    private static bool TryParse(string value, out DateOnly day)
    {
        day = default;
        return DayKeyPattern().IsMatch(value)
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    private static DateTimeOffset FromLocal(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (tz.IsInvalidTime(unspecified))
        {
            // Spring-forward gap: use the offset in force before the gap, so the time lands after the gap.
            offset = tz.GetUtcOffset(unspecified.AddDays(-1));
        }
        else if (tz.IsAmbiguousTime(unspecified))
        {
            // Fall-back overlap: the first occurrence has the larger (summer) offset.
            offset = tz.GetAmbiguousTimeOffsets(unspecified).Max();
        }
        else
        {
            offset = tz.GetUtcOffset(unspecified);
        }

        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
}
