using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Sheets;

namespace Huishoudplanner.Application.Export;

/// <summary>
/// Reads the query values of the four export endpoints (the zod schemas of <c>routes/export.ts</c>): every problem of a request is collected,
/// keyed by the query field, with the codes <c>required</c>, <c>invalid_iso_week</c>, <c>invalid_day_key</c> and <c>invalid_enum</c>.
/// </summary>
internal sealed class ExportQuery
{
    private readonly Dictionary<string, string[]> errors = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string[]> Errors => errors;

    public bool IsValid => errors.Count == 0;

    /// <summary>An ISO week label that names a real week (2027-W53 does not exist); the Monday it starts on.</summary>
    public DateOnly? Week(string field, string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            errors[field] = ["required"];
            return null;
        }

        if (DayKeys.MondayOfIsoWeek(raw) is { } monday)
        {
            return monday;
        }

        errors[field] = ["invalid_iso_week"];
        return null;
    }

    public DateOnly? Day(string field, string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            errors[field] = ["required"];
            return null;
        }

        if (DayKeys.IsDayKey(raw))
        {
            return DayKeys.Parse(raw);
        }

        errors[field] = ["invalid_day_key"];
        return null;
    }

    /// <summary>The number of weeks: only 1, 2 or 4.</summary>
    public int? WeekCount(string field, string? raw)
    {
        switch (raw)
        {
            case null:
                errors[field] = ["required"];
                return null;
            case "1":
                return 1;
            case "2":
                return 2;
            case "4":
                return 4;
            default:
                errors[field] = ["invalid_enum"];
                return null;
        }
    }

    public SheetOrientation Orientation(string field, string? raw)
    {
        switch (raw)
        {
            case null:
            case "portrait":
                return SheetOrientation.Portrait;
            case "landscape":
                return SheetOrientation.Landscape;
            default:
                errors[field] = ["invalid_enum"];
                return SheetOrientation.Portrait;
        }
    }

    public bool Flag(string field, string? raw)
    {
        switch (raw)
        {
            case null:
            case "false":
                return false;
            case "true":
                return true;
            default:
                errors[field] = ["invalid_enum"];
                return false;
        }
    }

    public SheetLanguage Language(string field, string? raw)
    {
        switch (raw)
        {
            case null:
            case "nl":
                return SheetLanguage.Nl;
            case "en":
                return SheetLanguage.En;
            default:
                errors[field] = ["invalid_enum"];
                return SheetLanguage.Nl;
        }
    }
}
