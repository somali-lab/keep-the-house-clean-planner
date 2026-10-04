using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Application.Settings;

/// <summary>The settings part of <c>seed()</c> in <c>apps/server/src/domain/seed.ts</c>, idempotent and audited as the system actor.</summary>
public sealed class SettingsSeedService(
    ForStoringSettings store,
    ForRecordingAudit audit,
    ForRunningTransactions transactions,
    TimeProvider time,
    HouseholdOptions household) : ISettingsSeedService
{
    public async Task<OneOf<SettingsSeedResult, ConflictError, PortError>> SeedAsync(CancellationToken cancellationToken)
    {
        var run = await transactions.RunAsync(SeedInTransactionAsync, cancellationToken).ConfigureAwait(false);
        return run.Match<OneOf<SettingsSeedResult, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<SettingsSeedResult, ConflictError, PortError>>(result => result, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<SettingsSeedResult, PortError>>> SeedInTransactionAsync(CancellationToken cancellationToken)
    {
        static TransactionOutcome<OneOf<SettingsSeedResult, PortError>> Abort(PortError error) => TransactionOutcome.Abort<OneOf<SettingsSeedResult, PortError>>(error);

        var read = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        if (read.TryPickT2(out var readError, out var rest))
        {
            return Abort(readError);
        }

        return rest.TryPickT0(out var current, out _)
            ? await AddShippedIntervalAsync(current, cancellationToken).ConfigureAwait(false)
            : await CreateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransactionOutcome<OneOf<SettingsSeedResult, PortError>>> CreateAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var settings = SettingsDefaults.ForNewInstallation(household.Timezone, DayKeys.Today(DayKeys.FindZone(household.Timezone), now), now);
        var inserted = await store.InsertIfMissingAsync(settings, cancellationToken).ConfigureAwait(false);
        if (inserted.TryPickT1(out var insertError, out var wasInserted))
        {
            return TransactionOutcome.Abort<OneOf<SettingsSeedResult, PortError>>(insertError);
        }

        if (!wasInserted)
        {
            return TransactionOutcome.Commit<OneOf<SettingsSeedResult, PortError>>(SettingsSeedResult.Unchanged);
        }

        var entry = ChangeSet.Between(null, SettingsAudit.ToAudit(settings))
            .ToEntry(AuditActor.System, AuditEntity.Settings, SettingsIds.Singleton, AuditAction.Create);
        return await RecordAsync(entry, SettingsSeedResult.Created, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransactionOutcome<OneOf<SettingsSeedResult, PortError>>> AddShippedIntervalAsync(
        HouseholdSettings current, CancellationToken cancellationToken)
    {
        var intervals = SettingsDefaults.WithThreePerWeek(current.Intervals);
        if (ReferenceEquals(intervals, current.Intervals))
        {
            return TransactionOutcome.Commit<OneOf<SettingsSeedResult, PortError>>(SettingsSeedResult.Unchanged);
        }

        var changes = new SettingsChanges { Intervals = intervals };
        var change = ChangeSet.Between(SettingsAudit.ToAudit(current), SettingsAudit.ToAudit(changes.ApplyTo(current)));
        var written = await store.UpdateAsync(changes, cancellationToken).ConfigureAwait(false);
        if (!written.TryPickT0(out _, out var writeFailure))
        {
            // A missing document vanished between the read and the write (the next start seeds it); an unconditional write has no version to conflict with.
            return TransactionOutcome.Abort<OneOf<SettingsSeedResult, PortError>>(writeFailure.Match<PortError>(
                _ => new PortError("settings.vanished: the settings disappeared while seeding."),
                error => error,
                _ => new PortError("settings.seed: an unconditional write reported a version conflict.")));
        }

        var entry = change.ToEntry(AuditActor.System, AuditEntity.Settings, SettingsIds.Singleton, AuditAction.Update);
        return await RecordAsync(entry, SettingsSeedResult.IntervalAdded, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransactionOutcome<OneOf<SettingsSeedResult, PortError>>> RecordAsync(
        AuditEntry entry, SettingsSeedResult result, CancellationToken cancellationToken)
    {
        var recorded = await audit.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<SettingsSeedResult, PortError>>(result),
            error => TransactionOutcome.Abort<OneOf<SettingsSeedResult, PortError>>(error));
    }
}
