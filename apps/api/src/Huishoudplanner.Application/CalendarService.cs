using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using OneOf;

namespace Huishoudplanner.Application;

public sealed class CalendarService(ForReadingCycleAnchor anchors, HouseholdOptions household) : ICalendarService
{
    public async Task<OneOf<CalendarView, ValidationErrors, SettingsMissing, PortError>> GetDaysAsync(
        string? firstDay, string? lastDay, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var start = ParseDay("from", firstDay, errors);
        var end = ParseDay("to", lastDay, errors);
        if (start is { } first && end is { } last)
        {
            if (first > last)
            {
                errors["from"] = ["from_after_to"];
            }
            else if (DayKeys.DaysBetween(first, last) + 1 > CalendarLimits.MaxRangeDaysValue)
            {
                errors["to"] = ["range_too_long"];
            }
        }

        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var anchor = await anchors.GetAnchorAsync(cancellationToken).ConfigureAwait(false);
        if (anchor.TryPickT1(out var missing, out var rest))
        {
            return missing;
        }

        if (rest.TryPickT1(out var failure, out var day))
        {
            return failure;
        }

        var days = Enumerable.Range(0, DayKeys.DaysBetween(start!.Value, end!.Value) + 1)
            .Select(offset => CalendarDay.Of(DayKeys.AddDays(start.Value, offset), day))
            .ToList();
        return new CalendarView(household.Timezone, days);
    }

    private static DateOnly? ParseDay(string field, string? value, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors[field] = ["required"];
            return null;
        }

        if (!DayKeys.IsDayKey(value))
        {
            errors[field] = ["invalid_day_key"];
            return null;
        }

        return DayKeys.Parse(value);
    }
}
