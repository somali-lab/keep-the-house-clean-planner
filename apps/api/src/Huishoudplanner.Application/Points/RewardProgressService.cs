using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Rewards;
using OneOf;

namespace Huishoudplanner.Application.Points;

/// <summary>
/// The progress of the reward meter (requirements 4.12; port of <c>pointsProgress</c>): the period is the one of today in the household timezone, the
/// earned points come from the ledger and the planned work is only read when the goal is automatic. Writes and audits nothing.
/// </summary>
public sealed class RewardProgressService(
    ForStoringSettings settings,
    ForStoringPointEntries entries,
    ForReadingPlannedWork plannedWork,
    TimeProvider time) : IRewardProgressService
{
    public async Task<OneOf<RewardProgress, ValidationErrors, SettingsMissing, PortError>> ProgressAsync(RewardProgressRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PointsRules.IsId(request.PersonId))
        {
            return new ValidationErrors(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["personId"] = ["invalid_object_id"] });
        }

        // Ids are 24-character hex strings in lower case everywhere in the API.
        request = request with { PersonId = request.PersonId.ToLowerInvariant() };
        var read = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryPickT0(out var household, out var readFailure))
        {
            return readFailure.Match<OneOf<RewardProgress, ValidationErrors, SettingsMissing, PortError>>(missing => missing, error => error);
        }

        var zone = DayKeys.FindZone(household.Timezone);
        var period = RewardProgressRules.PeriodOf(request.Period, DayKeys.Today(zone, time), household.CycleAnchorDate);
        var from = DayKeys.FromDayKey(period.Start, zone);
        var to = DayKeys.FromDayKey(DayKeys.AddDays(period.End, 1), zone);

        var totals = await entries.SumByPersonAsync(from, to, cancellationToken).ConfigureAwait(false);
        if (!totals.TryPickT0(out var sums, out var totalsFailure))
        {
            return totalsFailure;
        }

        // Earned is what executions and bonuses paid; a redemption in the same period must not lower it.
        var total = sums.FirstOrDefault(t => string.Equals(t.PersonId, request.PersonId, StringComparison.Ordinal));
        var earned = total is null ? 0 : total.Points + total.Redeemed;

        var automatic = new AutomaticGoal(0, 0);
        if (RewardProgressRules.ExplicitGoal(household, request.Period) is null)
        {
            var planned = await plannedWork.FindPlannedAsync(from, to, cancellationToken).ConfigureAwait(false);
            if (!planned.TryPickT0(out var work, out var plannedFailure))
            {
                return plannedFailure;
            }

            automatic = RewardMeter.AutomaticGoalFor(RewardProgressRules.GoalOccurrences(work, zone), request.PersonId, period);
        }

        return RewardProgressRules.Build(request, period, household, earned, automatic);
    }
}
