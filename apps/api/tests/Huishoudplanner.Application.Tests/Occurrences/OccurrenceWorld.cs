using Huishoudplanner.Application.Occurrences;
using Huishoudplanner.Application.Tests.Generation;
using Huishoudplanner.Application.Tests.Rooms;
using Huishoudplanner.Application.Tests.Settings;
using Huishoudplanner.Application.Tests.Tasks;
using Huishoudplanner.Application.Tests.Users;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Tasks;
using Huishoudplanner.Domain.Users;
using OneOf;
using FakeAudit = Huishoudplanner.Application.Tests.Rooms.FakeAudit;
using FixedClock = Huishoudplanner.Application.Tests.Settings.FixedClock;

namespace Huishoudplanner.Application.Tests.Occurrences;

/// <summary>Runs the work once; an aborted run restores occurrences, tasks and the audit log, like a rolled back transaction.</summary>
internal sealed class OccurrenceTransactions(FakeOccurrenceStore occurrences, FakeTaskStore tasks, FakeAudit audit) : ForRunningTransactions
{
    public int Aborts { get; private set; }

    public ConflictError? ConflictInsteadOfRunning { get; set; }

    public async Task<OneOf<T, ConflictError, PortError>> RunAsync<T>(
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken)
    {
        if (ConflictInsteadOfRunning is { } conflict)
        {
            return conflict;
        }

        var occurrencesBefore = occurrences.Items.ToList();
        var tasksBefore = tasks.Items.ToList();
        var auditBefore = audit.Entries.ToList();
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit)
        {
            Aborts++;
            occurrences.Items = occurrencesBefore;
            tasks.Items = tasksBefore;
            audit.Entries = auditBefore;
        }

        return outcome.Value;
    }
}

/// <summary>
/// The occurrence use cases with in-memory ports. The household starts on Monday 2026-09-14 (the settings anchor), cycle 0 and 1 are
/// generated and the clock stands on Wednesday 2026-09-16 10:00 Amsterdam, like the Node tests.
/// </summary>
internal sealed class OccurrenceWorld
{
    public static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    public static readonly TimeZoneInfo Zone = DayKeys.FindZone("Europe/Amsterdam");

    public FakeOccurrenceStore Occurrences { get; } = new();

    public FakeTaskStore TaskStore { get; } = new();

    public FakeCycleStore CycleStore { get; } = new();

    public UserWorld People { get; } = new();

    public FakeSettingsStore SettingsStore { get; } = new(SettingsSamples.Seeded());

    public FakeAudit Audit { get; } = new();

    public OccurrenceTransactions Transactions { get; }

    public FixedClock Clock { get; } = new(Now);

    public OccurrenceService Service { get; }

    public User P1 { get; }

    public User P2 { get; }

    public User Admin { get; }

    public HouseholdTask Weekly { get; }

    public HouseholdTask Twice { get; }

    public Cycle Cycle0 { get; }

    public Cycle Cycle1 { get; }

    public OccurrenceWorld()
    {
        Transactions = new OccurrenceTransactions(Occurrences, TaskStore, Audit);
        Service = new OccurrenceService(Occurrences, TaskStore, new FakeUserStore(People), SettingsStore, CycleStore, Transactions, Audit, Clock);
        P1 = People.Add("Persoon 1");
        P2 = People.Add("Persoon 2");
        Admin = People.Add("Beheerder", Role.Admin);
        Weekly = NewTask("Badkamer schoonmaken", 30);
        Twice = NewTask("Wastafel", 10);
        var anchor = new DateOnly(2026, 9, 14);
        Cycle0 = Cycle(0, anchor);
        Cycle1 = Cycle(1, anchor);
    }

    public static Actor Actor(User user) => new(user.Id, user.Role, ActorSource.Ui);

    public HouseholdTask NewTask(string name, int minutes, int? points = null)
    {
        var task = new HouseholdTask(TaskStore.NextId(), name, "0000000000000000000000aa", "1w", minutes, points ?? minutes, null, true, string.Empty, [], null, Now.AddDays(-30), Now.AddDays(-30));
        TaskStore.Items.Add(task);
        return task;
    }

    private Cycle Cycle(int index, DateOnly anchor)
    {
        var cycle = new Cycle(CycleStore.NextId(), index, Cycles.CycleStart(index, anchor), Cycles.CycleEnd(index, anchor), null, Now.AddDays(-2), "run");
        CycleStore.Items.Add(cycle);
        return cycle;
    }

    public static DateTimeOffset At(string day) => DayKeys.FromDayKey(DayKeys.Parse(day), Zone);

    /// <summary>An open occurrence of a task on a day, as generation creates it.</summary>
    public Occurrence Seed(HouseholdTask task, string day, User? assignee = null, OccurrenceStatus status = OccurrenceStatus.Open, Func<Occurrence, Occurrence>? tweak = null)
    {
        var date = At(day);
        var cycle = DayKeys.Parse(day) <= Cycle0.EndDate ? Cycle0 : Cycle1;
        var occurrence = new Occurrence(
            Occurrences.NextId(),
            task.Id,
            cycle.Id,
            "0000000000000000000000bb",
            date,
            date,
            assignee?.Id,
            status,
            null,
            null,
            null,
            null,
            task.DurationMinutes,
            task.Name,
            task.RoomId,
            "Badkamer",
            OccurrenceOrigin.Generated,
            Now.AddDays(-2),
            Now.AddDays(-2));
        occurrence = tweak?.Invoke(occurrence) ?? occurrence;
        Occurrences.Items.Add(occurrence);
        return occurrence;
    }

    public Occurrence Stored(Occurrence occurrence) => Occurrences.Items.Single(o => o.Id == occurrence.Id);

    public IEnumerable<AuditEntry> Entries(AuditEntity entity, AuditAction? action = null) =>
        Audit.Entries.Where(e => e.Entity == entity && (action is null || e.Action == action));

    /// <summary>Writes made through the occurrence and task stores, to prove that a refused request writes nothing.</summary>
    public int Writes => Occurrences.Updates + TaskStore.LastCompletedWrites + Audit.Entries.Count;
}
