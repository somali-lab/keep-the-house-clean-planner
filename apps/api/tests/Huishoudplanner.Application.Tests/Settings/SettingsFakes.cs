using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Concurrency;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Application.Tests.Settings;

/// <summary>A clock that stands still until a test moves it.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>An in-memory settings document that counts writes and can be rolled back with its transaction.</summary>
internal sealed class FakeSettingsStore(HouseholdSettings? initial) : ForStoringSettings
{
    private readonly DateTimeOffset writeTime = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    public HouseholdSettings? Document { get; set; } = initial;

    public int Writes { get; private set; }

    public PortError? Failure { get; set; }

    public Task<OneOf<HouseholdSettings, SettingsMissing, PortError>> GetAsync(CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<HouseholdSettings, SettingsMissing, PortError>>(failure);
        }

        return Task.FromResult<OneOf<HouseholdSettings, SettingsMissing, PortError>>(
            Document is null ? new SettingsMissing() : Document);
    }

    public Task<OneOf<HouseholdSettings, SettingsMissing, PortError, PreconditionFailed>> UpdateAsync(
        SettingsChanges changes, CancellationToken cancellationToken, int? expectedVersion = null)
    {
        if (Document is null)
        {
            return Task.FromResult<OneOf<HouseholdSettings, SettingsMissing, PortError, PreconditionFailed>>(new SettingsMissing());
        }

        if (EntityVersion.Check(expectedVersion, Document.Version) is { } stale)
        {
            return Task.FromResult<OneOf<HouseholdSettings, SettingsMissing, PortError, PreconditionFailed>>(stale);
        }

        Writes++;
        Document = changes.ApplyTo(Document) with { UpdatedAt = writeTime, Version = Document.Version + 1 };
        return Task.FromResult<OneOf<HouseholdSettings, SettingsMissing, PortError, PreconditionFailed>>(Document);
    }

    public Task<OneOf<bool, PortError>> InsertIfMissingAsync(HouseholdSettings settings, CancellationToken cancellationToken)
    {
        if (Document is not null)
        {
            return Task.FromResult<OneOf<bool, PortError>>(false);
        }

        Writes++;
        Document = settings;
        return Task.FromResult<OneOf<bool, PortError>>(true);
    }
}

internal sealed class FakeAudit : ForRecordingAudit
{
    public List<AuditEntry> Entries { get; } = [];

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

/// <summary>Runs the work once; an aborted run restores the store and the audit entries, like a rolled back transaction.</summary>
internal sealed class FakeTransactions(FakeSettingsStore store, FakeAudit audit) : ForRunningTransactions
{
    public ConflictError? ConflictAfterWork { get; set; }

    public int Runs { get; private set; }

    public async Task<OneOf<T, ConflictError, PortError>> RunAsync<T>(
        Func<CancellationToken, Task<TransactionOutcome<T>>> work, CancellationToken cancellationToken)
    {
        Runs++;
        var document = store.Document;
        var entries = audit.Entries.Count;
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit || ConflictAfterWork is not null)
        {
            store.Document = document;
            audit.Entries.RemoveRange(entries, audit.Entries.Count - entries);
        }

        if (ConflictAfterWork is { } conflict)
        {
            return conflict;
        }

        return outcome.Value;
    }
}

internal static class SettingsSamples
{
    public static readonly DateTimeOffset Created = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The settings of a fresh installation seeded on Monday 14 September 2026.</summary>
    public static HouseholdSettings Seeded() => SettingsDefaults.ForNewInstallation(
        "Europe/Amsterdam", new DateOnly(2026, 9, 14), Created);

    public static Interval Year => new("year", "1x per jaar", null, 365);
}
