using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

public interface ICalendarService
{
    /// <summary>The cycle, week and ISO week of every day from <paramref name="firstDay"/> to <paramref name="lastDay"/>, both included.</summary>
    /// <param name="firstDay">A <c>YYYY-MM-DD</c> day key, or null when the caller sent none.</param>
    /// <param name="lastDay">A <c>YYYY-MM-DD</c> day key, or null when the caller sent none.</param>
    Task<OneOf<CalendarView, ValidationErrors, SettingsMissing, PortError>> GetDaysAsync(
        string? firstDay, string? lastDay, CancellationToken cancellationToken);
}
