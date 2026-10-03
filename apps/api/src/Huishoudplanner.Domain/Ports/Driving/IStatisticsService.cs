using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Statistics;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The statistics reports and the statistics reset (requirements 4.7). The reports are open to every role; the reset is for administrators,
/// which the driving adapter decides. A period is the last <c>cycles</c> cycles (1 to 26, default 4) or the last <c>weeks</c> calendar weeks
/// (1 to 3, which wins over <c>cycles</c>); an out of range value is a <see cref="ValidationErrors"/> keyed by the parameter.
/// </summary>
public interface IStatisticsService
{
    Task<OneOf<WorkloadReport, ValidationErrors, SettingsMissing, PortError>> WorkloadAsync(int? cycles, int? weeks, CancellationToken cancellationToken);

    Task<OneOf<CompletionReport, ValidationErrors, SettingsMissing, PortError>> CompletionAsync(int? cycles, int? weeks, StatsGroupBy groupBy, CancellationToken cancellationToken);

    Task<OneOf<IntervalReport, ValidationErrors, SettingsMissing, PortError>> IntervalsAsync(int? cycles, int? weeks, CancellationToken cancellationToken);

    Task<OneOf<DeviationReport, ValidationErrors, SettingsMissing, PortError>> DeviationsAsync(int? cycles, int? weeks, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the execution history and writes the one audit entry, in one transaction. Without <paramref name="before"/> every occurrence resets to
    /// open; with it only data strictly older than that day is purged. A day after today is <see cref="BeforeInFuture"/>.
    /// </summary>
    Task<OneOf<StatisticsResetResult, BeforeInFuture, SettingsMissing, ConflictError, PortError>> ResetAsync(Actor actor, DateOnly? before, CancellationToken cancellationToken);
}
