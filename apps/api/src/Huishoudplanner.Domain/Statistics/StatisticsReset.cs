using System.Globalization;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Statistics;

/// <summary>A <c>before</c> day after today was asked for. Maps to <c>400 before_in_future</c>.</summary>
public readonly record struct BeforeInFuture;

/// <summary>
/// What a statistics reset does, decided by the domain (port of <c>resetStatistics</c> in <c>domain/stats.ts</c>). Without <c>before</c> it
/// starts over from today: every occurrence resets to open, except recorded extra work, which has no planned state and is deleted, and every
/// derived ledger entry and redemption goes. With <c>before</c> only occurrences, cycles, ledger entries and redemptions strictly older than
/// that day are purged; anything from <c>before</c> on is left as it is.
/// </summary>
/// <param name="Boundary">Midnight of the boundary day in the household timezone: everything dated before it is purged.</param>
/// <param name="BoundaryKey">The boundary day; it becomes the bonus floor (ADR-0012).</param>
/// <param name="BoundaryCycle">Cycles with a lower index are purged.</param>
/// <param name="RestartFromToday">Reset every non-open occurrence and task back to open, not just the ones being purged.</param>
/// <param name="BonusFloorBefore">The floor in the settings now, if any.</param>
/// <param name="BonusFloor">The floor after the reset: it only moves forward.</param>
/// <param name="Now">The modification time of everything the reset changes.</param>
public sealed record StatisticsResetPlan(
    DateTimeOffset Boundary,
    DateOnly BoundaryKey,
    int BoundaryCycle,
    bool RestartFromToday,
    DateOnly? BonusFloorBefore,
    DateOnly BonusFloor,
    DateTimeOffset Now)
{
    /// <summary>The floor is written only when the reset moves it.</summary>
    public bool MovesBonusFloor => BonusFloorBefore != BonusFloor;

    /// <summary>
    /// Whether the reset changed nothing at all: it removed and reopened nothing and the floor was already at, or beyond, the value it would be set
    /// to. Only such a reset is a no-op that writes and audits nothing; moving the floor is a state change of its own.
    /// </summary>
    public bool ChangesNothing(StatisticsResetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return !MovesBonusFloor && result.RemovedNothing();
    }

    public static OneOf.OneOf<StatisticsResetPlan, BeforeInFuture> Create(HouseholdSettings settings, DateTimeOffset now, DateOnly? before)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var zone = DayKeys.FindZone(settings.Timezone);
        var today = DayKeys.ToDayKey(now, zone);
        if (before is { } day && day > today)
        {
            return new BeforeInFuture();
        }

        var boundaryKey = before ?? today;
        // Periods that start before the boundary lost (part of) their history, so they never earn a bonus from what remains. The floor only
        // moves forward: a later purge before an earlier day does not give old periods their bonuses back.
        var floorBefore = settings.BonusFloor;
        var floor = floorBefore is { } existing && existing > boundaryKey ? existing : boundaryKey;
        return new StatisticsResetPlan(
            DayKeys.FromDayKey(boundaryKey, zone),
            boundaryKey,
            Cycles.CycleIndexFor(boundaryKey, settings.CycleAnchorDate),
            before is null,
            floorBefore,
            floor,
            now);
    }
}

/// <summary>
/// The numbers of a reset, as <c>DELETE /stats</c> answers them and the reset audit entry records them.
/// </summary>
/// <param name="DeletedOccurrences">Occurrences before the boundary.</param>
/// <param name="DeletedRecorded">Recorded extra work removed by a restart from today, because it has no planned state to return to.</param>
/// <param name="ResetOccurrences">Occurrences reopened by a restart from today.</param>
/// <param name="ResetTasks">Tasks whose last completion was cleared by a restart from today.</param>
/// <param name="DeletedPastCycles">Cycles before the boundary cycle.</param>
/// <param name="RemovedPointEntries">Entries of the points ledger that went with the history: executions, bonuses and redemptions (requirements 4.12).</param>
/// <param name="RemovedRedemptions">The redemptions among <see cref="RemovedPointEntries"/>.</param>
public sealed record StatisticsResetResult(
    int DeletedOccurrences,
    int DeletedRecorded,
    int ResetOccurrences,
    int ResetTasks,
    int DeletedPastCycles,
    int RemovedPointEntries,
    int RemovedRedemptions)
{
    /// <summary>
    /// Whether the reset removed and reopened nothing at all. It then starts no rebuild of the awards, and the answer still carries the zero
    /// counts; whether it is also a no-op without audit entry depends on the bonus floor (<see cref="StatisticsResetPlan.ChangesNothing"/>).
    /// A method, so it is not part of the serialized answer.
    /// </summary>
    public bool RemovedNothing() =>
        DeletedOccurrences == 0 && DeletedRecorded == 0 && ResetOccurrences == 0 && ResetTasks == 0
        && DeletedPastCycles == 0 && RemovedPointEntries == 0 && RemovedRedemptions == 0;
}

/// <summary>The one audit entry of a reset, as the Node server writes it: on the settings, action <c>reset</c>, the counts in <c>meta</c>.</summary>
public static class StatisticsResetAudit
{
    public static AuditEntry ForReset(AuditActor actor, StatisticsResetPlan plan, StatisticsResetResult result, string resetId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(result);
        var before = new List<KeyValuePair<string, AuditValue>> { new("statistics", "bestaande uitvoeringsgeschiedenis") };
        if (plan.BonusFloorBefore is { } floorBefore)
        {
            before.Add(new("bonusFloor", Day(floorBefore)));
        }

        return new AuditEntry(
            actor,
            AuditEntity.Settings,
            SettingsIds.Singleton,
            AuditAction.Reset,
            new AuditObject(before),
            AuditObject.Of(
                ("statistics", plan.RestartFromToday ? "opnieuw gestart" : "oude data opgeschoond"),
                ("bonusFloor", Day(plan.BonusFloor))),
            AuditObject.Of(
                ("deletedOccurrences", result.DeletedOccurrences),
                ("deletedRecorded", result.DeletedRecorded),
                ("resetOccurrences", result.ResetOccurrences),
                ("resetTasks", result.ResetTasks),
                ("deletedPastCycles", result.DeletedPastCycles),
                ("removedPointEntries", result.RemovedPointEntries),
                ("removedRedemptions", result.RemovedRedemptions),
                ("resetId", resetId),
                ("scoped", !plan.RestartFromToday)));
    }

    private static AuditValue Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
