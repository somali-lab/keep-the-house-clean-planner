using Huishoudplanner.Application.Statistics;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Statistics;
using Moq;
using OneOf;

namespace Huishoudplanner.Application.Tests.Statistics;

/// <summary>
/// The statistics use cases with in-memory ports. The household is Europe/Amsterdam, the anchor is Monday 2026-09-14 (cycle 0 is 14 Sep to
/// 11 Oct, cycle 1 is 12 Oct to 8 Nov) and the clock stands on Wednesday 14 Oct 2026. The transaction fake rolls the audit log back when the
/// work aborts, so a test can see that a reset never leaves an audit entry without its writes or the other way round.
/// </summary>
internal sealed class StatisticsWorld
{
    public const string Cycle0 = "c00000000000000000000000";
    public const string Cycle1 = "c00000000000000000000001";
    public const string P1 = "a00000000000000000000001";
    public const string P2 = "a00000000000000000000002";
    public const string TaskA = "b00000000000000000000001";
    public const string Room = "d00000000000000000000001";

    public static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    public static readonly Actor Admin = new("0123456789abcdef01234567", Role.Admin, ActorSource.Ui);

    public StatisticsWorld()
    {
        Settings = SettingsDefaults.ForNewInstallation("Europe/Amsterdam", new DateOnly(2026, 9, 14), Now.AddDays(-30));
        Cycles = [
            new Cycle(Cycle0, 0, new DateOnly(2026, 9, 14), new DateOnly(2026, 10, 11), null, Now, "run"),
            new Cycle(Cycle1, 1, new DateOnly(2026, 10, 12), new DateOnly(2026, 11, 8), null, Now, "run"),
        ];
        Reader = new FakeReader();
        Resetter = new FakeResetter(Audit);
        Transactions = new FakeTransactions(Audit);
        Service = new StatisticsService(SettingsStore.Object, CycleStore.Object, Reader, Resetter, Transactions, Audit, new FixedClock(Now), Badges);
        SettingsStore.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult<OneOf<HouseholdSettings, SettingsMissing, PortError>>(SettingsFailure is { } f ? f : Settings is null ? new SettingsMissing() : Settings));
        CycleStore.Setup(c => c.ListAsync(It.IsAny<CycleCursor?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((CycleCursor? after, int take, CancellationToken _) =>
                Task.FromResult<OneOf<IReadOnlyList<Cycle>, PortError>>(OneOf<IReadOnlyList<Cycle>, PortError>.FromT0([.. Cycles.OrderBy(c => c.Index).Where(c => after is null || c.Index > after.Index).Take(take)])));
    }

    public HouseholdSettings? Settings { get; set; }

    public PortError? SettingsFailure { get; set; }

    public List<Cycle> Cycles { get; set; }

    public Mock<ForStoringSettings> SettingsStore { get; } = new();

    public Mock<ForStoringCycles> CycleStore { get; } = new();

    public FakeReader Reader { get; }

    public FakeResetter Resetter { get; }

    public FakeAudit Audit { get; } = new();

    public Badges.RecordingBadgeAwards Badges { get; } = new();

    public FakeTransactions Transactions { get; }

    public StatisticsService Service { get; }

    public static StatisticsOccurrence Occurrence(string cycle, string? task, DateOnly day, string? assignee, OccurrenceStatus status, int minutes = 30, string? completedBy = null) =>
        new(
            cycle,
            task,
            DayKeys.FromDayKey(day, DayKeys.FindZone("Europe/Amsterdam")),
            null,
            assignee,
            status,
            status == OccurrenceStatus.Done ? DayKeys.FromDayKey(day, DayKeys.FindZone("Europe/Amsterdam")).AddHours(10) : null,
            completedBy,
            minutes,
            "Badkamer schoonmaken",
            Room,
            false);
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class FakeReader : ForReadingStatistics
{
    public List<StatisticsOccurrence> Occurrences { get; } = [];

    public List<StatisticsPerson> People { get; } = [new(StatisticsWorld.P1, "Persoon 1", true), new(StatisticsWorld.P2, "Persoon 2", true)];

    public List<StatisticsTask> Tasks { get; } = [new(StatisticsWorld.TaskA, "Badkamer schoonmaken", StatisticsWorld.Room, "1w", true)];

    public List<StatisticsRoom> Rooms { get; } = [new(StatisticsWorld.Room, "Badkamer")];

    public PortError? Failure { get; set; }

    public int OccurrenceReads { get; private set; }

    public IReadOnlyCollection<string>? AskedCycleIds { get; private set; }

    public DateTimeOffset? AskedFrom { get; private set; }

    public DateTimeOffset? AskedTo { get; private set; }

    public Task<OneOf<IReadOnlyList<StatisticsOccurrence>, PortError>> FindOccurrencesAsync(IReadOnlyCollection<string> cycleIds, DateTimeOffset? rangeStart, DateTimeOffset? rangeEnd, CancellationToken cancellationToken)
    {
        OccurrenceReads++;
        (AskedCycleIds, AskedFrom, AskedTo) = (cycleIds, rangeStart, rangeEnd);
        return Task.FromResult(Failure is { } failure
            ? (OneOf<IReadOnlyList<StatisticsOccurrence>, PortError>)failure
            : OneOf<IReadOnlyList<StatisticsOccurrence>, PortError>.FromT0([.. Occurrences.Where(o => cycleIds.Contains(o.CycleId))]));
    }

    public Task<OneOf<IReadOnlyList<StatisticsPerson>, PortError>> ListPeopleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Failure is { } failure ? (OneOf<IReadOnlyList<StatisticsPerson>, PortError>)failure : OneOf<IReadOnlyList<StatisticsPerson>, PortError>.FromT0(People));

    public Task<OneOf<IReadOnlyList<StatisticsTask>, PortError>> ListTasksAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Failure is { } failure ? (OneOf<IReadOnlyList<StatisticsTask>, PortError>)failure : OneOf<IReadOnlyList<StatisticsTask>, PortError>.FromT0(Tasks));

    public Task<OneOf<IReadOnlyList<StatisticsRoom>, PortError>> ListRoomsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Failure is { } failure ? (OneOf<IReadOnlyList<StatisticsRoom>, PortError>)failure : OneOf<IReadOnlyList<StatisticsRoom>, PortError>.FromT0(Rooms));
}

internal sealed class FakeResetter(FakeAudit audit) : ForResettingStatistics
{
    public static readonly StatisticsResetResult Counts = new(4, 2, 3, 1, 1, 6, 2);

    public List<StatisticsResetPlan> Plans { get; } = [];

    /// <summary>What the reset did at the moment it ran: the audit log must not hold its entry yet.</summary>
    public int AuditEntriesWhenRun { get; private set; } = -1;

    public PortError? Failure { get; set; }

    public Task<OneOf<StatisticsResetResult, PortError>> ResetAsync(StatisticsResetPlan plan, CancellationToken cancellationToken)
    {
        Plans.Add(plan);
        AuditEntriesWhenRun = audit.Entries.Count;
        return Task.FromResult(Failure is { } failure ? (OneOf<StatisticsResetResult, PortError>)failure : Counts);
    }
}

internal sealed class FakeAudit : ForRecordingAudit
{
    public List<AuditEntry> Entries { get; set; } = [];

    public PortError? Failure { get; set; }

    public Task<OneOf<Success, PortError>> RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<Success, PortError>>(failure);
        }

        Entries.Add(entry);
        return Task.FromResult<OneOf<Success, PortError>>(new Success());
    }
}

internal sealed class FakeTransactions(FakeAudit audit) : ForRunningTransactions
{
    public int Runs { get; private set; }

    public int Aborts { get; private set; }

    public ConflictError? ConflictInsteadOfRunning { get; set; }

    public async Task<OneOf<T, ConflictError, PortError>> RunAsync<T>(Func<CancellationToken, Task<TransactionOutcome<T>>> work, CancellationToken cancellationToken)
    {
        if (ConflictInsteadOfRunning is { } conflict)
        {
            return conflict;
        }

        Runs++;
        var before = audit.Entries.ToList();
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit)
        {
            Aborts++;
            audit.Entries = before;
        }

        return outcome.Value;
    }
}
