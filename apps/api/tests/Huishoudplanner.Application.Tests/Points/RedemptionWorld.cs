using Huishoudplanner.Application.Points;
using Huishoudplanner.Application.Tests.Occurrences;
using Huishoudplanner.Application.Tests.Users;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>
/// The redemption store on the in-memory ledger of <see cref="FakePointEntryStore"/>, with the semantics of the Mongo store: the request key is
/// unique, a duplicate is <see cref="RequestKeyTaken"/>, the balance is the sum over the whole ledger and the guard is a counter per person.
/// </summary>
internal sealed class FakeRedemptionStore(FakePointEntryStore ledger) : ForStoringRedemptions
{
    private readonly Dictionary<string, string> requestKeys = [];

    private int counter;

    private Action? raceOnNextInsert;

    private readonly List<PointEntry> committedByOthers = [];

    public Dictionary<string, int> Guards { get; } = [];

    /// <summary>The order of the calls that matter for the guard: the guard must come before the balance read.</summary>
    public List<string> Calls { get; } = [];

    public int Inserts { get; private set; }

    public int Deletes { get; private set; }

    /// <summary>A key a record holds that no read can see (removed in between again and again): every insert of it loses.</summary>
    public string? AlwaysTakenKey { get; set; }

    public PortError? Failure { get; set; }

    /// <summary>Runs once, inside the next insert, before the key is checked: the winner of a race for a request key commits then.</summary>
    public void RaceOnNextInsert(string requestKey, string person, int points, string? note)
    {
        raceOnNextInsert = () =>
        {
            var winner = Make(person, points, note, requestKey);
            ledger.Items.Add(winner);
            committedByOthers.Add(winner);
        };
    }

    /// <summary>What a transaction that rolled back must not lose: the entries other transactions committed meanwhile.</summary>
    public List<PointEntry> TakeCommittedByOthers()
    {
        var taken = committedByOthers.ToList();
        committedByOthers.Clear();
        return taken;
    }

    /// <summary>A redemption the store already holds, as an earlier request left it.</summary>
    public PointEntry Seed(string person, int points, string? note = null, string? requestKey = null, DateTimeOffset? date = null)
    {
        var entry = Make(person, points, note, requestKey, date);
        ledger.Items.Add(entry);
        return entry;
    }

    public PointEntry SeedEarned(string person, int amount, PointEntryKind kind = PointEntryKind.Execution)
    {
        var id = (0xd00000 + (++counter)).ToString("x24", System.Globalization.CultureInfo.InvariantCulture);
        var entry = new PointEntry(
            id, "execution:" + id, kind, person, amount, OccurrenceWorld.At("2026-09-15"), OccurrenceWorld.At("2026-09-14"), null, null, null, "Klus",
            PointEntrySource.Live, null, null, null, OccurrenceWorld.Now, OccurrenceWorld.Now);
        ledger.Items.Add(entry);
        return entry;
    }

    public Task<OneOf<PointEntry, NotFound, PortError>> FindByRequestIdAsync(string requestId, CancellationToken cancellationToken)
    {
        Calls.Add("find-by-request-id");
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(failure);
        }

        var found = ledger.Items.FirstOrDefault(e => requestKeys.TryGetValue(e.Id, out var key) && key == requestId);
        return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(found is null ? new NotFound() : found);
    }

    public Task<OneOf<PointEntry, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(failure);
        }

        var found = ledger.Items.FirstOrDefault(e => e.Id == id && e.Kind == PointEntryKind.Redemption);
        return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(found is null ? new NotFound() : found);
    }

    public Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Failure is { } failure
            ? OneOf<long, PortError>.FromT1(failure)
            : ledger.Items.Count(e => e.Kind == PointEntryKind.Redemption));

    public Task<OneOf<long, PortError>> BalanceOfAsync(string personId, CancellationToken cancellationToken)
    {
        Calls.Add("balance");
        return Task.FromResult(Failure is { } failure
            ? OneOf<long, PortError>.FromT1(failure)
            : ledger.Items.Where(e => e.PersonId == personId).Sum(e => (long)e.Amount));
    }

    public Task<OneOf<Success, PortError>> LockBalanceAsync(string personId, CancellationToken cancellationToken)
    {
        Calls.Add("guard");
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<Success, PortError>>(failure);
        }

        Guards[personId] = Guards.GetValueOrDefault(personId) + 1;
        return Task.FromResult<OneOf<Success, PortError>>(new Success());
    }

    public Task<OneOf<PointEntry, RequestKeyTaken, PortError>> InsertAsync(NewRedemption draft, CancellationToken cancellationToken)
    {
        Calls.Add("insert");
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<PointEntry, RequestKeyTaken, PortError>>(failure);
        }

        if (raceOnNextInsert is { } race)
        {
            raceOnNextInsert = null;
            race();
        }

        if (draft.RequestId is { } key && (key == AlwaysTakenKey || ledger.Items.Any(e => requestKeys.TryGetValue(e.Id, out var held) && held == key)))
        {
            return Task.FromResult<OneOf<PointEntry, RequestKeyTaken, PortError>>(new RequestKeyTaken());
        }

        Inserts++;
        var entry = Make(draft.PersonId, draft.Points, draft.Note, draft.RequestId, draft.Date) with
        {
            WeekStart = draft.WeekStart,
            CentsPerPointSnapshot = draft.CentsPerPoint,
            CurrencyCodeSnapshot = draft.CurrencyCode,
            CreatedAt = draft.At,
            UpdatedAt = draft.At,
        };
        ledger.Items.Add(entry);
        return Task.FromResult<OneOf<PointEntry, RequestKeyTaken, PortError>>(entry);
    }

    public Task<OneOf<PointEntry, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        Calls.Add("delete");
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(failure);
        }

        var found = ledger.Items.FirstOrDefault(e => e.Id == id && e.Kind == PointEntryKind.Redemption);
        if (found is null)
        {
            return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(new NotFound());
        }

        ledger.Items.Remove(found);
        Deletes++;
        return Task.FromResult<OneOf<PointEntry, NotFound, PortError>>(found);
    }

    private PointEntry Make(string person, int points, string? note, string? requestKey, DateTimeOffset? date = null)
    {
        var id = (0xc00000 + (++counter)).ToString("x24", System.Globalization.CultureInfo.InvariantCulture);
        if (requestKey is not null)
        {
            requestKeys[id] = requestKey;
        }

        var day = date ?? OccurrenceWorld.At("2026-09-16");
        return new PointEntry(
            id, "redemption:" + id, PointEntryKind.Redemption, person, -points, day, OccurrenceWorld.At("2026-09-14"), null, null, null, string.Empty,
            PointEntrySource.Live, note, 0, "EUR", OccurrenceWorld.Now, OccurrenceWorld.Now);
    }
}

/// <summary>Runs the work once; an aborted run restores the ledger and the audit log, like a rolled back transaction, but keeps what other transactions committed.</summary>
internal sealed class RedemptionTransactions(FakePointEntryStore ledger, FakeRedemptionStore store, Rooms.FakeAudit audit) : ForRunningTransactions
{
    public int Runs { get; private set; }

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

        Runs++;
        var ledgerBefore = ledger.Items.ToList();
        var auditBefore = audit.Entries.ToList();
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit)
        {
            Aborts++;
            ledger.Items = [.. ledgerBefore, .. store.TakeCommittedByOthers()];
            audit.Entries = auditBefore;
        }

        return outcome.Value;
    }
}

/// <summary>The redemption use cases on in-memory ports, next to the occurrence world (same people, settings, audit log and clock: Wednesday 2026-09-16 10:00 Amsterdam).</summary>
internal sealed class RedemptionWorld
{
    public const string KeyA = "redeem-key-aaaaaaaaaaaa";

    public const string KeyB = "redeem-key-bbbbbbbbbbbb";

    public OccurrenceWorld Occ { get; } = new();

    public FakePointEntryStore Ledger { get; } = new();

    public FakeRedemptionStore Store { get; }

    public RedemptionTransactions Transactions { get; }

    public RedemptionService Service { get; }

    public User P1 => Occ.P1;

    public User P2 => Occ.P2;

    public User Admin => Occ.Admin;

    public RedemptionWorld()
    {
        Store = new FakeRedemptionStore(Ledger);
        Transactions = new RedemptionTransactions(Ledger, Store, Occ.Audit);
        Service = new RedemptionService(Store, Occ.SettingsStore, new FakeUserStore(Occ.People), Transactions, Occ.Audit, Occ.Clock);
    }

    public static Actor ActorOf(User user) => OccurrenceWorld.Actor(user);

    public IEnumerable<AuditEntry> PointsAudit(AuditAction? action = null) => Occ.Entries(AuditEntity.Points, action);

    public IReadOnlyList<PointEntry> Redemptions => [.. Ledger.Items.Where(e => e.Kind == PointEntryKind.Redemption)];

    /// <summary>Moves the clock to an instant.</summary>
    public void SetNow(DateTimeOffset instant) => Occ.Clock.Now = instant;

    public static DateTimeOffset At(string day) => DayKeys.FromDayKey(DayKeys.Parse(day), OccurrenceWorld.Zone);
}
