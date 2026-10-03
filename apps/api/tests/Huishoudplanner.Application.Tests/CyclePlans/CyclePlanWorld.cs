using Huishoudplanner.Application.CyclePlans;
using FakeAudit = Huishoudplanner.Application.Tests.Rooms.FakeAudit;
using FakeRooms = Huishoudplanner.Application.Tests.Rooms.FakeRooms;
using FixedClock = Huishoudplanner.Application.Tests.Rooms.FixedClock;
using Huishoudplanner.Application.Tests.Settings;
using Huishoudplanner.Application.Tests.Tasks;
using Huishoudplanner.Application.Tests.Users;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Application.Tests.CyclePlans;

/// <summary>An in-memory plan store (oldest first) that counts writes and can fail.</summary>
internal sealed class FakeCyclePlanStore : ForStoringCyclePlans
{
    private int counter;

    public List<CyclePlan> Items { get; set; } = [];

    public int Writes { get; private set; }

    public PortError? Failure { get; set; }

    public string NextId() => (++counter).ToString("x24", System.Globalization.CultureInfo.InvariantCulture);

    private IEnumerable<CyclePlan> Ordered => Items.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id, StringComparer.Ordinal);

    public Task<OneOf<IReadOnlyList<CyclePlan>, PortError>> ListAsync(CyclePlanCursor? after, int take, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<CyclePlan>, PortError>>(failure);
        }

        var page = Ordered
            .Where(p => after is null || p.CreatedAt > after.CreatedAt || (p.CreatedAt == after.CreatedAt && string.CompareOrdinal(p.Id, after.Id) > 0))
            .Take(take)
            .ToList();
        return Task.FromResult<OneOf<IReadOnlyList<CyclePlan>, PortError>>(page);
    }

    public Task<OneOf<CyclePlan, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken) =>
        One(Items.FirstOrDefault(p => p.Id == id));

    public Task<OneOf<CyclePlan, NotFound, PortError>> FindActiveAsync(CancellationToken cancellationToken) =>
        One(Items.FirstOrDefault(p => p.Active));

    public Task<OneOf<CyclePlan, NotFound, PortError>> FindDefaultAsync(CancellationToken cancellationToken) =>
        One(Ordered.FirstOrDefault());

    private Task<OneOf<CyclePlan, NotFound, PortError>> One(CyclePlan? plan) =>
        Task.FromResult<OneOf<CyclePlan, NotFound, PortError>>(
            Failure is { } failure ? failure : plan is null ? new NotFound() : plan);

    public Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<long, PortError>>(Failure is { } failure ? failure : Items.Count);

    public Task<OneOf<CyclePlan, PortError>> InsertAsync(NewCyclePlan plan, CancellationToken cancellationToken)
    {
        Writes++;
        var stored = new CyclePlan(
            NextId(), plan.Name, plan.Active, plan.Slots, plan.WeekThemes, false, PlanSources.Manual, null, null, false, plan.CreatedAt, plan.CreatedAt);
        Items.Add(stored);
        return Task.FromResult<OneOf<CyclePlan, PortError>>(stored);
    }

    public Task<OneOf<CyclePlan, NotFound, PortError>> UpdateMetaAsync(string id, PlanMetaChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken) =>
        Replace(id, p => p with { Name = changes.Name ?? p.Name, WeekThemes = changes.WeekThemes ?? p.WeekThemes, UpdatedAt = updatedAt });

    public Task<OneOf<CyclePlan, NotFound, PortError>> ReplaceSlotsAsync(string id, IReadOnlyList<CyclePlanSlot> slots, DateTimeOffset updatedAt, CancellationToken cancellationToken) =>
        Replace(id, p => p with { Slots = slots, UpdatedAt = updatedAt });

    private Task<OneOf<CyclePlan, NotFound, PortError>> Replace(string id, Func<CyclePlan, CyclePlan> change)
    {
        var index = Items.FindIndex(p => p.Id == id);
        if (index < 0)
        {
            return Task.FromResult<OneOf<CyclePlan, NotFound, PortError>>(new NotFound());
        }

        Writes++;
        Items[index] = change(Items[index]);
        return Task.FromResult<OneOf<CyclePlan, NotFound, PortError>>(Items[index]);
    }

    public Task<OneOf<Success, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var removed = Items.RemoveAll(p => p.Id == id);
        if (removed > 0)
        {
            Writes++;
        }

        return Task.FromResult<OneOf<Success, NotFound, PortError>>(removed > 0 ? new Success() : new NotFound());
    }
}

/// <summary>Runs the work once; an aborted run restores the plans and the audit entries, like a rolled back transaction.</summary>
internal sealed class PlanTransactions(FakeCyclePlanStore plans, FakeAudit audit) : ForRunningTransactions
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

        var plansBefore = plans.Items.ToList();
        var auditBefore = audit.Entries.ToList();
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit)
        {
            Aborts++;
            plans.Items = plansBefore;
            audit.Entries = auditBefore;
        }

        return outcome.Value;
    }
}

/// <summary>The plan use cases with hand-written in-memory ports for plans, tasks, rooms, people and settings.</summary>
internal sealed class CyclePlanWorld
{
    public static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);

    public static readonly Actor Planner = new("0123456789abcdef01234567", Role.Planner, ActorSource.Ui);

    public FakeCyclePlanStore Plans { get; } = new();

    public FakeTaskStore Tasks { get; } = new();

    public FakeRooms RoomStore { get; } = new();

    public UserWorld People { get; } = new();

    public FakeSettingsStore Settings { get; } = new(SettingsSamples.Seeded());

    public FakeAudit Audit { get; } = new();

    public PlanTransactions Transactions { get; }

    public FixedClock Clock { get; } = new(Now);

    public CyclePlanService Service { get; }

    public CyclePlanSeedService Seed { get; }

    public CyclePlanWorld()
    {
        Transactions = new PlanTransactions(Plans, Audit);
        Service = new CyclePlanService(Plans, Tasks, new FakeUserStore(People), RoomStore, Settings, Transactions, Audit, Clock);
        Seed = new CyclePlanSeedService(Plans, Audit, Transactions, Clock);
    }

    public Huishoudplanner.Domain.Rooms.Room Room(string name) =>
        AddRoom(new Huishoudplanner.Domain.Rooms.Room(RoomStore.NextId(), name, 10, true, false, Now.AddDays(-1), Now.AddDays(-1)));

    private Huishoudplanner.Domain.Rooms.Room AddRoom(Huishoudplanner.Domain.Rooms.Room room)
    {
        RoomStore.Items.Add(room);
        return room;
    }

    public Huishoudplanner.Domain.Tasks.HouseholdTask Task(string name, string roomId, string intervalKey = "1w", int minutes = 30, bool active = true)
    {
        var task = new Huishoudplanner.Domain.Tasks.HouseholdTask(
            Tasks.NextId(), name, roomId, intervalKey, minutes, minutes, null, active, string.Empty, [], null, Now.AddDays(-1), Now.AddDays(-1));
        Tasks.Items.Add(task);
        return task;
    }

    public User Person(string name, bool active = true, int[]? unavailable = null)
    {
        var user = People.Add(name, Role.Member, active);
        People.Users[^1] = user with { UnavailableWeekdays = unavailable ?? [] };
        return People.Users[^1];
    }

    /// <summary>A stored plan created <paramref name="daysAgo"/> days before <see cref="Now"/> (the oldest is the default plan).</summary>
    public CyclePlan Plan(string name, bool active = false, int daysAgo = 1, params CyclePlanSlot[] slots)
    {
        var created = Now.AddDays(-daysAgo);
        var plan = new CyclePlan(Plans.NextId(), name, active, slots, CyclePlanRules.EmptyWeekThemes, false, PlanSources.Manual, null, null, false, created, created);
        Plans.Items.Add(plan);
        return plan;
    }
}
