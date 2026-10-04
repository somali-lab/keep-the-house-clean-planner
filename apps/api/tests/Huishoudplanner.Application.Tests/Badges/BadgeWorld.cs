using System.Globalization;
using Huishoudplanner.Application.Badges;
using Huishoudplanner.Application.Tests.Rooms;
using Huishoudplanner.Application.Tests.Tasks;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OneOf;

namespace Huishoudplanner.Application.Tests.Badges;

/// <summary>
/// The badge use cases and the award evaluation with hand-written in-memory ports. The transaction fake rolls the badges, the awards and the audit
/// log back when the work aborts, so a test can see that nothing survives a failure. Two people, a toilet and a mop task, the clock on Wednesday
/// 2026-09-16 10:00 Amsterdam.
/// </summary>
internal sealed class BadgeWorld
{
    public static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    public static readonly Actor Admin = new("0123456789abcdef01234567", Role.Admin, ActorSource.Ui);

    public const string P1 = "b00000000000000000000001";
    public const string P2 = "b00000000000000000000002";

    public FakeBadgeStore Badges { get; } = new();

    public FakeAwardStore Awards { get; } = new();

    public FakeEvidence Evidence { get; } = new();

    public FakeTaskStore Tasks { get; } = new();

    public FakeAudit Audit { get; } = new();

    public BadgeTransactions Transactions { get; }

    public FixedClock Clock { get; } = new(Now);

    public BadgeAwardService Evaluation { get; }

    public BadgeService Service { get; }

    public HouseholdTask Toilet { get; }

    public HouseholdTask Mop { get; }

    public BadgeWorld()
    {
        Transactions = new BadgeTransactions(Badges, Awards, Audit);
        Evaluation = new BadgeAwardService(Badges, Awards, Evidence, Transactions, Audit, Clock, NullLogger<BadgeAwardService>.Instance);
        // The use cases call the real evaluation, so a badge change is followed by its awards like in the application.
        Service = new BadgeService(Badges, Tasks, Evidence, Awards, Evaluation, Transactions, Audit, Clock);
        Toilet = NewTask("Toilet schoonmaken");
        Mop = NewTask("Vloer dweilen");
    }

    public HouseholdTask NewTask(string name, bool active = true)
    {
        var task = new HouseholdTask(Tasks.NextId(), name, "0000000000000000000000aa", "1w", 10, 10, null, active, string.Empty, [], null, Now.AddDays(-30), Now.AddDays(-30));
        Tasks.Items.Add(task);
        return task;
    }

    public Badge Seed(string name, BadgeRule rule, bool active = true, string? exampleKey = null, BadgeImageInfo? image = null, int minutesAfter = 0)
    {
        var at = Now.AddDays(-10).AddMinutes(minutesAfter);
        var badge = new Badge(Badges.NextId(), name, string.Empty, rule, active, exampleKey, image, at, at);
        Badges.Items.Add(badge);
        return badge;
    }

    public BadgeAward SeedAward(Badge badge, string person, DateTimeOffset at)
    {
        var award = new BadgeAward(Awards.NextId(), BadgeAward.KeyOf(badge.Id, person), badge.Id, person, at, Now.AddDays(-5), Now.AddDays(-5));
        Awards.Items.Add(award);
        return award;
    }

    public static BadgeRule Executions(int threshold, params string[] tasks) => new(BadgeRuleType.Executions, tasks, threshold);

    public static BadgeRule Minutes(int threshold, params string[] tasks) => new(BadgeRuleType.Minutes, tasks, threshold);

    public void Done(string person, string task, DateTimeOffset at, int minutes = 10, string? id = null) =>
        Evidence.Executions.Add(new CreditedExecution(person, new BadgeExecution(id ?? Evidence.NextId(), task, minutes, at)));

    public IEnumerable<AuditEntry> Entries(AuditEntity entity, AuditAction? action = null) =>
        Audit.Entries.Where(e => e.Entity == entity && (action is null || e.Action == action));
}

internal sealed class FakeBadgeStore : ForStoringBadges
{
    private int counter;

    public List<Badge> Items { get; set; } = [];

    public PortError? Failure { get; set; }

    public bool FailWrites { get; set; }

    public int Writes { get; private set; }

    public Dictionary<string, BadgeImageData> Images { get; } = [];

    public string NextId() => (0xd00000 + ++counter).ToString("x24", CultureInfo.InvariantCulture);

    private static IEnumerable<Badge> Ordered(IEnumerable<Badge> badges) =>
        badges.OrderBy(b => b.CreatedAt.ToUnixTimeMilliseconds()).ThenBy(b => b.Id, StringComparer.Ordinal);

    public Task<OneOf<IReadOnlyList<Badge>, PortError>> ListAsync(bool? active, BadgeCursor? after, int take, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<Badge>, PortError>>(failure);
        }

        IReadOnlyList<Badge> page = [.. Ordered(Items.Where(b => active is null || b.Active == active))
            .Where(b => after is null || (b.CreatedAt.ToUnixTimeMilliseconds(), b.Id).CompareTo((after.CreatedAtMs, after.Id)) > 0)
            .Take(take)];
        return Task.FromResult<OneOf<IReadOnlyList<Badge>, PortError>>(OneOf<IReadOnlyList<Badge>, PortError>.FromT0(page));
    }

    public Task<OneOf<IReadOnlyList<Badge>, PortError>> ListAllAsync(bool? active, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<Badge>, PortError>>(failure);
        }

        IReadOnlyList<Badge> all = [.. Ordered(Items.Where(b => active is null || b.Active == active))];
        return Task.FromResult<OneOf<IReadOnlyList<Badge>, PortError>>(OneOf<IReadOnlyList<Badge>, PortError>.FromT0(all));
    }

    public Task<OneOf<Badge, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<Badge, NotFound, PortError>>(failure);
        }

        var badge = Items.FirstOrDefault(b => b.Id == id);
        return Task.FromResult<OneOf<Badge, NotFound, PortError>>(badge is null ? new NotFound() : badge);
    }

    public Task<OneOf<Badge, NotFound, PortError>> FindByExampleKeyAsync(string exampleKey, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<Badge, NotFound, PortError>>(failure);
        }

        var badge = Items.FirstOrDefault(b => b.ExampleKey == exampleKey);
        return Task.FromResult<OneOf<Badge, NotFound, PortError>>(badge is null ? new NotFound() : badge);
    }

    public Task<OneOf<int, PortError>> CountAsync(CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<int, PortError>>(Failure is { } failure ? failure : Items.Count);

    public Task<OneOf<IReadOnlyList<Badge>, PortError>> FindNamingTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Badge> named = [.. Ordered(Items.Where(b => b.Rule.TaskIds.Contains(taskId)))];
        return Task.FromResult<OneOf<IReadOnlyList<Badge>, PortError>>(Failure is { } failure ? failure : OneOf<IReadOnlyList<Badge>, PortError>.FromT0(named));
    }

    public Task<OneOf<BadgeImageData, NotFound, PortError>> FindImageAsync(string id, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<BadgeImageData, NotFound, PortError>>(failure);
        }

        return Task.FromResult<OneOf<BadgeImageData, NotFound, PortError>>(Images.TryGetValue(id, out var image) ? image : new NotFound());
    }

    public Task<OneOf<Badge, PortError>> InsertAsync(NewBadge badge, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            return Task.FromResult<OneOf<Badge, PortError>>(new PortError("fake write failure"));
        }

        Writes++;
        var stored = new Badge(NextId(), badge.Name, badge.Description, badge.Rule, badge.Active, badge.ExampleKey, badge.Image?.Info, badge.CreatedAt, badge.CreatedAt);
        Items.Add(stored);
        if (badge.Image is { } image)
        {
            Images[stored.Id] = image;
        }

        return Task.FromResult<OneOf<Badge, PortError>>(stored);
    }

    public Task<OneOf<Badge, NotFound, PortError>> UpdateAsync(string id, BadgeChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            return Task.FromResult<OneOf<Badge, NotFound, PortError>>(new PortError("fake write failure"));
        }

        var index = Items.FindIndex(b => b.Id == id);
        if (index < 0)
        {
            return Task.FromResult<OneOf<Badge, NotFound, PortError>>(new NotFound());
        }

        Writes++;
        var current = Items[index];
        var updated = current with
        {
            Name = changes.Name ?? current.Name,
            Description = changes.Description ?? current.Description,
            Rule = changes.Rule ?? current.Rule,
            Active = changes.Active ?? current.Active,
            Image = changes.ClearImage ? null : changes.Image?.Info ?? current.Image,
            UpdatedAt = updatedAt,
        };
        if (changes.Image is { } image)
        {
            Images[id] = image;
        }
        else if (changes.ClearImage)
        {
            Images.Remove(id);
        }

        Items[index] = updated;
        return Task.FromResult<OneOf<Badge, NotFound, PortError>>(updated);
    }

    public Task<OneOf<Success, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            return Task.FromResult<OneOf<Success, NotFound, PortError>>(new PortError("fake write failure"));
        }

        if (Items.RemoveAll(b => b.Id == id) == 0)
        {
            return Task.FromResult<OneOf<Success, NotFound, PortError>>(new NotFound());
        }

        Writes++;
        Images.Remove(id);
        return Task.FromResult<OneOf<Success, NotFound, PortError>>(new Success());
    }
}

internal sealed class FakeAwardStore : ForStoringBadgeAwards
{
    private int counter;

    public List<BadgeAward> Items { get; set; } = [];

    public PortError? Failure { get; set; }

    public PortError? WriteFailure { get; set; }

    public int Applies { get; private set; }

    public string NextId() => (0xe00000 + ++counter).ToString("x24", CultureInfo.InvariantCulture);

    public Task<OneOf<IReadOnlyList<BadgeAward>, PortError>> ListAsync(BadgeAwardQuery query, int take, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<BadgeAward>, PortError>>(failure);
        }

        IReadOnlyList<BadgeAward> page = [.. Items
            .Where(a => query.PersonId is null || a.PersonId == query.PersonId)
            .OrderBy(a => a.AwardedAt.ToUnixTimeMilliseconds()).ThenBy(a => a.Id, StringComparer.Ordinal)
            .Where(a => query.After is null || (a.AwardedAt.ToUnixTimeMilliseconds(), a.Id).CompareTo((query.After.AwardedAtMs, query.After.Id)) > 0)
            .Take(take)];
        return Task.FromResult<OneOf<IReadOnlyList<BadgeAward>, PortError>>(OneOf<IReadOnlyList<BadgeAward>, PortError>.FromT0(page));
    }

    public Task<OneOf<IReadOnlyList<BadgeAward>, PortError>> FindForEvaluationAsync(IReadOnlyCollection<string>? personIds, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<BadgeAward>, PortError>>(failure);
        }

        IReadOnlyList<BadgeAward> found = [.. Items.Where(a => personIds is null || personIds.Contains(a.PersonId))];
        return Task.FromResult<OneOf<IReadOnlyList<BadgeAward>, PortError>>(OneOf<IReadOnlyList<BadgeAward>, PortError>.FromT0(found));
    }

    public Task<OneOf<IReadOnlyList<AppliedAward>, PortError>> ApplyAsync(BadgeAwardPlan plan, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if ((WriteFailure ?? Failure) is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<AppliedAward>, PortError>>(failure);
        }

        Applies++;
        var applied = new List<AppliedAward>();
        foreach (var current in plan.Deletes)
        {
            if (Items.RemoveAll(a => a.Id == current.Id && a.AwardedAt == current.AwardedAt) > 0)
            {
                applied.Add(new AppliedAward(AwardChange.Removed, current));
            }
        }

        foreach (var move in plan.Updates)
        {
            var index = Items.FindIndex(a => a.Id == move.Current.Id && a.AwardedAt == move.Current.AwardedAt);
            if (index >= 0)
            {
                Items[index] = Items[index] with { AwardedAt = move.AwardedAt, UpdatedAt = at };
                applied.Add(new AppliedAward(AwardChange.Updated, Items[index], move.Current));
            }
        }

        foreach (var insert in plan.Inserts)
        {
            var award = new BadgeAward(NextId(), BadgeAward.KeyOf(insert.BadgeId, insert.PersonId), insert.BadgeId, insert.PersonId, insert.AwardedAt, at, at);
            Items.Add(award);
            applied.Add(new AppliedAward(AwardChange.Created, award));
        }

        return Task.FromResult<OneOf<IReadOnlyList<AppliedAward>, PortError>>(OneOf<IReadOnlyList<AppliedAward>, PortError>.FromT0(applied));
    }
}

internal sealed class FakeEvidence : ForReadingBadgeEvidence
{
    private int counter;

    public List<CreditedExecution> Executions { get; } = [];

    public List<OnTimeWeek> Weeks { get; } = [];

    public PortError? Failure { get; set; }

    public int ExecutionReads { get; private set; }

    public int WeekReads { get; private set; }

    /// <summary>The people the executions were asked for, per read (<see langword="null"/> for everybody).</summary>
    public List<IReadOnlyCollection<string>?> ExecutionReadsFor { get; } = [];

    public string NextId() => (0xf00000 + ++counter).ToString("x24", CultureInfo.InvariantCulture);

    public Task<OneOf<IReadOnlyList<CreditedExecution>, PortError>> FindCreditedExecutionsAsync(IReadOnlyCollection<string>? personIds, CancellationToken cancellationToken)
    {
        ExecutionReads++;
        ExecutionReadsFor.Add(personIds);
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<CreditedExecution>, PortError>>(failure);
        }

        IReadOnlyList<CreditedExecution> found = [.. Executions.Where(e => personIds is null || personIds.Contains(e.PersonId))];
        return Task.FromResult<OneOf<IReadOnlyList<CreditedExecution>, PortError>>(OneOf<IReadOnlyList<CreditedExecution>, PortError>.FromT0(found));
    }

    public Task<OneOf<IReadOnlyList<OnTimeWeek>, PortError>> FindOnTimeWeeksAsync(IReadOnlyCollection<string>? personIds, CancellationToken cancellationToken)
    {
        WeekReads++;
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<OnTimeWeek>, PortError>>(failure);
        }

        IReadOnlyList<OnTimeWeek> found = [.. Weeks.Where(w => personIds is null || personIds.Contains(w.PersonId))];
        return Task.FromResult<OneOf<IReadOnlyList<OnTimeWeek>, PortError>>(OneOf<IReadOnlyList<OnTimeWeek>, PortError>.FromT0(found));
    }
}

/// <summary>Runs the work once; an aborted run restores the badges, the awards and the audit log, like a rolled back transaction.</summary>
internal sealed class BadgeTransactions(FakeBadgeStore badges, FakeAwardStore awards, FakeAudit audit) : ForRunningTransactions
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
        var badgesBefore = badges.Items.ToList();
        var imagesBefore = badges.Images.ToDictionary(p => p.Key, p => p.Value);
        var awardsBefore = awards.Items.ToList();
        var auditBefore = audit.Entries.ToList();
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit)
        {
            Aborts++;
            badges.Items = badgesBefore;
            badges.Images.Clear();
            foreach (var (key, value) in imagesBefore)
            {
                badges.Images[key] = value;
            }

            awards.Items = awardsBefore;
            audit.Entries = auditBefore;
        }

        return outcome.Value;
    }
}
