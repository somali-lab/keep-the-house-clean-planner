using System.Globalization;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Points;

/// <summary>
/// Reads the queries of the points reads from strings, so that a malformed value is a field-keyed <c>400 validation_error</c> and never a framework
/// binding failure (the Zod schemas of the Node server did the same). Only the syntax is checked here; the rules (a range that runs backwards, one
/// that is too long, the limit, the cursor) belong to the use case.
/// </summary>
internal static class PointsRequestParser
{
    public static OneOf<(DateOnly? From, DateOnly? To), ValidationErrors> ParseBalances(string? from, string? to)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var fromDay = OptionalDay(from, "from", errors);
        var toDay = OptionalDay(to, "to", errors);
        return errors.Count > 0 ? new ValidationErrors(errors) : (fromDay, toDay);
    }

    public static OneOf<PointEntriesRequest, ValidationErrors> ParseEntries(string? personId, string? from, string? to, string? limit, string? cursor)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (personId is null)
        {
            errors["personId"] = ["is required"];
        }

        var fromDay = RequiredDay(from, "from", errors);
        var toDay = RequiredDay(to, "to", errors);
        int? limitValue = null;
        if (limit is not null)
        {
            if (int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                limitValue = parsed;
            }
            else
            {
                errors["limit"] = ["Must be an integer."];
            }
        }

        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new PointEntriesRequest(personId!, fromDay!.Value, toDay!.Value, limitValue, cursor);
    }

    private static DateOnly? OptionalDay(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (DayKeys.IsDayKey(value))
        {
            return DayKeys.Parse(value);
        }

        errors[field] = ["invalid_day_key"];
        return null;
    }

    private static DateOnly? RequiredDay(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            errors[field] = ["is required"];
            return null;
        }

        return OptionalDay(value, field, errors);
    }
}
