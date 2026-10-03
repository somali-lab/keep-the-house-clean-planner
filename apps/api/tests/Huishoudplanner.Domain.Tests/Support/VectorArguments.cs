using System.Globalization;
using Huishoudplanner.Domain.Calendar;

namespace Huishoudplanner.Domain.Tests.Support;

/// <summary>
/// Reads the named arguments of a vector case. Exception mapping of the vectors' <c>RangeError</c>:
/// an unparsable day key becomes <see cref="FormatException"/>; an unknown timezone, a fractional number where the
/// C# parameter is an <c>int</c>, and every other range violation become <see cref="ArgumentOutOfRangeException"/>.
/// </summary>
public static class VectorArguments
{
    public static DateOnly Day(this VectorCase vectorCase, string name) =>
        DayKeys.Parse(vectorCase.Input.GetProperty(name).GetString()!);

    public static string Text(this VectorCase vectorCase, string name) =>
        vectorCase.Input.GetProperty(name).GetString()!;

    public static TimeZoneInfo Zone(this VectorCase vectorCase, string name) =>
        DayKeys.FindZone(vectorCase.Input.GetProperty(name).GetString()!);

    public static DateTimeOffset Instant(this VectorCase vectorCase, string name) =>
        DateTimeOffset.Parse(
            vectorCase.Input.GetProperty(name).GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>A JSON number as <c>int</c>; a fractional number cannot be passed to an <c>int</c> parameter.</summary>
    public static int WholeNumber(this VectorCase vectorCase, string name)
    {
        var number = vectorCase.Input.GetProperty(name).GetDouble();
        return number == Math.Floor(number)
            ? (int)number
            : throw new ArgumentOutOfRangeException(name, number, "Expected an integer.");
    }
}
