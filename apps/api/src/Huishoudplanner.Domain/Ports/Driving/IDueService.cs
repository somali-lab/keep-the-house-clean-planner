using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

public interface IDueService
{
    /// <summary>
    /// The ranked list of every active task with its due state, a page at a time (highest ratio first, then most days since, then task id).
    /// A task that is fine is in it as well (state <c>ok</c>). Needs no actor.
    /// </summary>
    /// <param name="limit">1 to <see cref="DueListQuery.MaxLimit"/>; null means <see cref="DueListQuery.DefaultLimit"/>.</param>
    /// <param name="cursor">The <c>NextCursor</c> of the previous page.</param>
    Task<OneOf<DueList, ValidationErrors, SettingsMissing, PortError>> GetDueAsync(int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>How many tasks are due and overdue today, counted over the whole list (what the generation answer reports).</summary>
    Task<OneOf<DueSummary, SettingsMissing, PortError>> GetSummaryAsync(CancellationToken cancellationToken);
}
