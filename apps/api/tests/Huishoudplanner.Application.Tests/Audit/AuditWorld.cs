using Huishoudplanner.Application.Audit;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Application.Tests.Audit;

/// <summary>The audit read, clear and retention use cases with hand-written in-memory ports.</summary>
internal sealed class AuditWorld
{
    public static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);

    public FakeAuditStore Store { get; } = new();

    public FakeOccurrences Occurrences { get; } = new();

    public FakeRetention Retention { get; } = new();

    public Clock Time { get; } = new(Now);

    public AuditLogService Log { get; }

    public AuditRetentionService RetentionJob { get; }

    public AuditWorld()
    {
        Log = new AuditLogService(Store, Occurrences, Store);
        RetentionJob = new AuditRetentionService(Retention, Store, Time);
    }

    private int counter;

    public AuditLogEntry Add(DateTimeOffset at, string entity = "room", string? entityId = null, string actorId = "0123456789abcdef01234567", string source = "ui", AuditObject? meta = null)
    {
        var entry = new AuditLogEntry(
            (++counter).ToString("x24", System.Globalization.CultureInfo.InvariantCulture),
            at,
            actorId,
            entity,
            entityId ?? (1000 + counter).ToString("x24", System.Globalization.CultureInfo.InvariantCulture),
            "update",
            AuditObject.Empty,
            AuditObject.Empty,
            source,
            meta);
        Store.Entries.Add(entry);
        return entry;
    }
}

internal sealed class Clock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeAuditStore : ForReadingAuditLog, ForDeletingAuditEntries
{
    public List<AuditLogEntry> Entries { get; } = [];

    public PortError? Failure { get; set; }

    public List<DateTimeOffset> Cutoffs { get; } = [];

    public int Clears { get; private set; }

    public (AuditLogFilter Filter, AuditCursor? After, int Take)? LastQuery { get; private set; }

    public Task<OneOf<IReadOnlyList<AuditLogEntry>, PortError>> ListAsync(AuditLogFilter filter, AuditCursor? after, int take, CancellationToken cancellationToken)
    {
        LastQuery = (filter, after, take);
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<AuditLogEntry>, PortError>>(failure);
        }

        IReadOnlyList<AuditLogEntry> found = [.. Entries
            .Where(e => filter.Entity is null || e.Entity == AuditNames.ToWire(filter.Entity.Value))
            .Where(e => filter.EntityId is null || e.EntityId == filter.EntityId)
            .Where(e => filter.ActorId is null || e.ActorId == filter.ActorId)
            .Where(e => filter.Source is null || e.Source == AuditNames.ToWire(filter.Source.Value))
            .Where(e => filter.From is null || e.At >= filter.From)
            .Where(e => filter.To is null || e.At <= filter.To)
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .Where(e => after is null || e.At < after.At || (e.At == after.At && string.CompareOrdinal(e.Id, after.Id) < 0))
            .Take(take)];
        return Task.FromResult<OneOf<IReadOnlyList<AuditLogEntry>, PortError>>(OneOf<IReadOnlyList<AuditLogEntry>, PortError>.FromT0(found));
    }

    public Task<OneOf<int, PortError>> ClearAsync(CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<int, PortError>>(failure);
        }

        Clears++;
        var count = Entries.Count;
        Entries.Clear();
        return Task.FromResult<OneOf<int, PortError>>(count);
    }

    public Task<OneOf<int, PortError>> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        Cutoffs.Add(cutoff);
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<int, PortError>>(failure);
        }

        return Task.FromResult<OneOf<int, PortError>>(Entries.RemoveAll(e => e.At < cutoff));
    }
}

internal sealed class FakeOccurrences : ForReadingOccurrenceContext
{
    public Dictionary<string, OccurrenceContext> Items { get; } = [];

    public PortError? Failure { get; set; }

    public List<IReadOnlyCollection<string>> Lookups { get; } = [];

    public Task<OneOf<IReadOnlyDictionary<string, OccurrenceContext>, PortError>> FindAsync(IReadOnlyCollection<string> occurrenceIds, CancellationToken cancellationToken)
    {
        Lookups.Add(occurrenceIds);
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyDictionary<string, OccurrenceContext>, PortError>>(failure);
        }

        IReadOnlyDictionary<string, OccurrenceContext> found = occurrenceIds.Where(Items.ContainsKey).ToDictionary(id => id, id => Items[id]);
        return Task.FromResult<OneOf<IReadOnlyDictionary<string, OccurrenceContext>, PortError>>(OneOf<IReadOnlyDictionary<string, OccurrenceContext>, PortError>.FromT0(found));
    }
}

internal sealed class FakeRetention : ForReadingAuditRetention
{
    public AuditRetentionPolicy Policy { get; set; } = new(null);

    public PortError? Failure { get; set; }

    public Task<OneOf<AuditRetentionPolicy, PortError>> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<AuditRetentionPolicy, PortError>>(Failure is { } failure ? failure : Policy);
}
