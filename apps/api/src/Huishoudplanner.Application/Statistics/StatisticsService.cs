using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Statistics;
using OneOf;

namespace Huishoudplanner.Application.Statistics;

/// <summary>
/// The statistics reports and the statistics reset (requirements 4.7). Port of <c>routes/stats.ts</c> and <c>domain/stats.ts</c>: the use case
/// reads the cycles, the occurrences of the period and the names it needs, and the domain calculates. The reset runs in one transaction with its
/// audit entry.
/// </summary>
public sealed class StatisticsService(
    ForStoringSettings settings,
    ForStoringCycles cycles,
    ForReadingStatistics reader,
    ForResettingStatistics resetter,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time,
    IBadgeAwardService badges) : IStatisticsService
{
    /// <summary>The most calendar weeks a period may span (<c>statsCyclesQuerySchema</c>: <c>weeks</c> 1 to 3).</summary>
    public const int MaxWeeks = 3;

    private const int CyclePageSize = CycleListQuery.MaxLimit;

    private sealed record Loaded(HouseholdSettings Settings, StatisticsScope Scope, IReadOnlyList<StatisticsOccurrence> Occurrences);

    public Task<OneOf<WorkloadReport, ValidationErrors, SettingsMissing, PortError>> WorkloadAsync(int? cycles, int? weeks, CancellationToken cancellationToken) =>
        ReportAsync<WorkloadReport>(cycles, weeks, async (loaded, ct) =>
        {
            if (loaded.Scope.Cycles.Count == 0)
            {
                return new WorkloadReport([]);
            }

            var people = await reader.ListPeopleAsync(ct).ConfigureAwait(false);
            return people.Match<OneOf<WorkloadReport, PortError>>(
                list => StatisticsCalculator.Workload(loaded.Scope, loaded.Occurrences, list),
                error => error);
        }, cancellationToken);

    public Task<OneOf<CompletionReport, ValidationErrors, SettingsMissing, PortError>> CompletionAsync(int? cycles, int? weeks, StatsGroupBy groupBy, CancellationToken cancellationToken) =>
        ReportAsync<CompletionReport>(cycles, weeks, async (loaded, ct) =>
        {
            if (loaded.Scope.Cycles.Count == 0)
            {
                return StatisticsCalculator.Completion(loaded.Scope, [], groupBy, default, [], [], []);
            }

            var tasks = await reader.ListTasksAsync(ct).ConfigureAwait(false);
            if (tasks.TryPickT1(out var tasksError, out var taskList))
            {
                return tasksError;
            }

            var rooms = await reader.ListRoomsAsync(ct).ConfigureAwait(false);
            if (rooms.TryPickT1(out var roomsError, out var roomList))
            {
                return roomsError;
            }

            var people = await reader.ListPeopleAsync(ct).ConfigureAwait(false);
            if (people.TryPickT1(out var peopleError, out var personList))
            {
                return peopleError;
            }

            var startOfToday = DayKeys.FromDayKey(DayKeys.ToDayKey(time.GetUtcNow(), loaded.Scope.Zone), loaded.Scope.Zone);
            return StatisticsCalculator.Completion(loaded.Scope, loaded.Occurrences, groupBy, startOfToday, taskList, roomList, personList);
        }, cancellationToken);

    public Task<OneOf<IntervalReport, ValidationErrors, SettingsMissing, PortError>> IntervalsAsync(int? cycles, int? weeks, CancellationToken cancellationToken) =>
        ReportAsync<IntervalReport>(cycles, weeks, async (loaded, ct) =>
        {
            var tasks = await reader.ListTasksAsync(ct).ConfigureAwait(false);
            return tasks.Match<OneOf<IntervalReport, PortError>>(
                list => StatisticsCalculator.Intervals(loaded.Scope, loaded.Occurrences, list, loaded.Settings.Intervals),
                error => error);
        }, cancellationToken);

    public Task<OneOf<DeviationReport, ValidationErrors, SettingsMissing, PortError>> DeviationsAsync(int? cycles, int? weeks, CancellationToken cancellationToken) =>
        ReportAsync<DeviationReport>(
            cycles,
            weeks,
            (loaded, _) => Task.FromResult<OneOf<DeviationReport, PortError>>(StatisticsCalculator.Deviations(loaded.Scope, loaded.Occurrences)),
            cancellationToken);

    /// <summary>Validates the period, loads the cycles and occurrences it covers and hands them to the report.</summary>
    private async Task<OneOf<TReport, ValidationErrors, SettingsMissing, PortError>> ReportAsync<TReport>(
        int? cycleCount,
        int? weeks,
        Func<Loaded, CancellationToken, Task<OneOf<TReport, PortError>>> calculate,
        CancellationToken cancellationToken)
    {
        if (ValidatePeriod(cycleCount, weeks) is { } invalid)
        {
            return invalid;
        }

        var period = new StatisticsPeriod(cycleCount ?? HouseholdLimits.Current.Statistics.DefaultCycles, weeks);
        var loaded = await LoadAsync(period, cancellationToken).ConfigureAwait(false);
        if (loaded.TryPickT1(out var missing, out var rest))
        {
            return missing;
        }

        if (rest.TryPickT1(out var failure, out var data))
        {
            return failure;
        }

        var report = await calculate(data, cancellationToken).ConfigureAwait(false);
        return report.Match<OneOf<TReport, ValidationErrors, SettingsMissing, PortError>>(r => r, error => error);
    }

    private async Task<OneOf<Loaded, SettingsMissing, PortError>> LoadAsync(StatisticsPeriod period, CancellationToken ct)
    {
        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        if (read.TryPickT1(out var missing, out var rest))
        {
            return missing;
        }

        if (rest.TryPickT1(out var settingsError, out var household))
        {
            return settingsError;
        }

        var all = new List<Cycle>();
        CycleCursor? after = null;
        while (true)
        {
            var page = await cycles.ListAsync(after, CyclePageSize, ct).ConfigureAwait(false);
            if (page.TryPickT1(out var cyclesError, out var items))
            {
                return cyclesError;
            }

            all.AddRange(items);
            if (items.Count < CyclePageSize)
            {
                break;
            }

            after = CycleCursor.After(items[^1]);
        }

        var scope = StatisticsScope.Select(all, household.Timezone, household.CycleAnchorDate, time.GetUtcNow(), period);
        if (scope.Cycles.Count == 0)
        {
            return new Loaded(household, scope, []);
        }

        var occurrences = await reader.FindOccurrencesAsync([.. scope.Cycles.Select(c => c.Id)], scope.From, scope.To, ct).ConfigureAwait(false);
        return occurrences.Match<OneOf<Loaded, SettingsMissing, PortError>>(
            list => new Loaded(household, scope, list),
            error => error);
    }

    private static ValidationErrors? ValidatePeriod(int? cycles, int? weeks)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var maxCycles = HouseholdLimits.Current.Statistics.MaxCycles;
        if (cycles is < 1 || cycles > maxCycles)
        {
            errors["cycles"] = [$"must be a whole number from 1 to {maxCycles}"];
        }

        if (weeks is < 1 or > MaxWeeks)
        {
            errors["weeks"] = [$"must be a whole number from 1 to {MaxWeeks}"];
        }

        return errors.Count == 0 ? null : new ValidationErrors(errors);
    }

    public async Task<OneOf<StatisticsResetResult, BeforeInFuture, SettingsMissing, ConflictError, PortError>> ResetAsync(Actor actor, DateOnly? before, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var ran = await transactions.RunAsync(ct => ResetInTransactionAsync(actor, before, ct), cancellationToken).ConfigureAwait(false);
        if (ran.IsT0 && ran.AsT0.IsT0)
        {
            // The awards follow the history they are derived from (ADR-0014): they are rebuilt from what remains, after the reset has committed.
            // A failure there is logged and never fails the reset; the next reconciliation repairs the awards.
            await badges.ReconcileSafelyAsync(AuditActor.From(actor), BadgeEvalTrigger.Reset, null, cancellationToken).ConfigureAwait(false);
        }

        return ran.Match<OneOf<StatisticsResetResult, BeforeInFuture, SettingsMissing, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<StatisticsResetResult, BeforeInFuture, SettingsMissing, ConflictError, PortError>>(
                result => result, future => future, missing => missing, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<StatisticsResetResult, BeforeInFuture, SettingsMissing, PortError>>> ResetInTransactionAsync(Actor actor, DateOnly? before, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<StatisticsResetResult, BeforeInFuture, SettingsMissing, PortError>> Abort(OneOf<StatisticsResetResult, BeforeInFuture, SettingsMissing, PortError> value) =>
            TransactionOutcome.Abort(value);

        // The settings are read inside the transaction: a retried attempt plans the reset against the floor it finds then.
        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        if (read.TryPickT1(out var missing, out var rest))
        {
            return Abort(missing);
        }

        if (rest.TryPickT1(out var settingsError, out var household))
        {
            return Abort(settingsError);
        }

        if (StatisticsResetPlan.Create(household, time.GetUtcNow(), before).TryPickT1(out var future, out var plan))
        {
            return Abort(future);
        }

        var reset = await resetter.ResetAsync(plan, ct).ConfigureAwait(false);
        if (reset.TryPickT1(out var resetError, out var result))
        {
            return Abort(resetError);
        }

        var entry = StatisticsResetAudit.ForReset(AuditActor.From(actor), plan, result, Guid.NewGuid().ToString("D"));
        var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<StatisticsResetResult, BeforeInFuture, SettingsMissing, PortError>>(result),
            error => Abort(error));
    }
}
