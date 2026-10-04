using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Concurrency;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Application.Tests.Users;

/// <summary>The in-memory state behind the fakes: the users, the audit log and what the use case did to them.</summary>
internal sealed class UserWorld
{
    public static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    public List<User> Users { get; } = [];

    public List<AuditEntry> Audit { get; } = [];

    public int Inserts { get; set; }

    public int Updates { get; set; }

    public int TransactionRuns { get; set; }

    public PortError? ListFailure { get; set; }

    public PortError? AuditFailure { get; set; }

    public PortError? InsertFailure { get; set; }

    public User Add(string name, Role role = Role.Member, bool active = true, int minutesAfterEpoch = 0)
    {
        var user = new User(
            Guid.NewGuid().ToString("N")[..24],
            name,
            "#000000",
            active,
            role,
            [],
            UserDefaults.NewUserMinutes,
            UserDefaults.NewUserMinutes,
            BrowserNotifications.Disabled,
            Now.AddMinutes(minutesAfterEpoch),
            Now.AddMinutes(minutesAfterEpoch));
        Users.Add(user);
        return user;
    }

    public static Actor ActorOf(User user, ActorSource source = ActorSource.Ui) => new(user.Id, user.Role, source);
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class FakeUserStore(UserWorld world) : ForStoringUsers
{
    public Task<OneOf<UserPage, PortError>> ListAsync(UserQuery query, CancellationToken cancellationToken)
    {
        if (world.ListFailure is { } failure)
        {
            return Task.FromResult<OneOf<UserPage, PortError>>(failure);
        }

        var ordered = world.Users
            .Where(u => query.Active is null || u.Active == query.Active)
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id, StringComparer.Ordinal)
            .Where(u => query.After is null || u.CreatedAt > query.After.CreatedAt ||
                        (u.CreatedAt == query.After.CreatedAt && string.CompareOrdinal(u.Id, query.After.Id) > 0))
            .Take(query.Limit + 1)
            .ToList();
        var items = ordered.Take(query.Limit).ToList();
        var next = ordered.Count > query.Limit ? UserCursor.For(items[^1]).Encode() : null;
        return Task.FromResult<OneOf<UserPage, PortError>>(new UserPage(items, next));
    }

    public Task<OneOf<User, NotFound, PortError>> FindAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<User, NotFound, PortError>>(
            world.Users.FirstOrDefault(u => u.Id == userId) is { } user ? user : new NotFound());

    public Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<long, PortError>>(world.Users.Count);

    public Task<OneOf<long, PortError>> CountOtherActiveAdminsAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<long, PortError>>(world.Users.Count(u => u.Id != userId && u.Active && u.Role == Role.Admin));

    public Task<OneOf<User, PortError>> InsertAsync(NewUser user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (world.InsertFailure is { } failure)
        {
            return Task.FromResult<OneOf<User, PortError>>(failure);
        }

        world.Inserts++;
        var stored = new User(
            Guid.NewGuid().ToString("N")[..24], user.Name, user.Color, true, user.Role, user.UnavailableWeekdays,
            user.DailyBudgetMinutes, user.MaxDailyMinutes, BrowserNotifications.Disabled, now, now, 1);
        world.Users.Add(stored);
        return Task.FromResult<OneOf<User, PortError>>(stored);
    }

    public Task<OneOf<Success, NotFound, PortError, PreconditionFailed>> UpdateAsync(
        string userId, UserPatch patch, DateTimeOffset now, CancellationToken cancellationToken, int? expectedVersion = null)
    {
        var index = world.Users.FindIndex(u => u.Id == userId);
        if (index < 0)
        {
            return Task.FromResult<OneOf<Success, NotFound, PortError, PreconditionFailed>>(new NotFound());
        }

        if (EntityVersion.Check(expectedVersion, world.Users[index].Version) is { } stale)
        {
            return Task.FromResult<OneOf<Success, NotFound, PortError, PreconditionFailed>>(stale);
        }

        world.Updates++;
        world.Users[index] = UserRules.Apply(world.Users[index], patch, now) with { Version = world.Users[index].Version + 1 };
        return Task.FromResult<OneOf<Success, NotFound, PortError, PreconditionFailed>>(new Success());
    }
}

internal sealed class FakeAudit(UserWorld world) : ForRecordingAudit
{
    public Task<OneOf<Success, PortError>> RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        if (world.AuditFailure is { } failure)
        {
            return Task.FromResult<OneOf<Success, PortError>>(failure);
        }

        world.Audit.Add(entry);
        return Task.FromResult<OneOf<Success, PortError>>(new Success());
    }
}

/// <summary>Runs the work once; an aborting outcome restores the users and the audit log, like a rolled back transaction.</summary>
internal sealed class FakeTransactions(UserWorld world) : ForRunningTransactions
{
    public async Task<OneOf<T, ConflictError, PortError>> RunAsync<T>(
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken)
    {
        world.TransactionRuns++;
        var users = world.Users.ToList();
        var audit = world.Audit.ToList();
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit)
        {
            world.Users.Clear();
            world.Users.AddRange(users);
            world.Audit.Clear();
            world.Audit.AddRange(audit);
        }

        return outcome.Value;
    }
}
