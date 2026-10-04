using System.Globalization;
using Huishoudplanner.Application.Points;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Transfer;
using Microsoft.Extensions.Logging;
using OneOf;

namespace Huishoudplanner.Application.Transfer;

/// <summary>
/// The JSON export and import (requirements 8; <c>routes/transfer.ts</c> and <c>domain/transfer.ts</c> of the Node server). The import checks the whole file
/// before it writes anything and replaces everything in one transaction together with its audit entry; the ledger of points is rebuilt afterwards, as in
/// the Node server, by the reconciliation (ADR-0011), which also rebuilds the badge awards where it owns them. A rebuild that fails is logged and the
/// nightly run repeats it: the data is replaced and audited by then.
/// </summary>
public sealed class TransferService(
    ForTransferringData data,
    ForStoringSettings settings,
    HouseholdOptions household,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    IPointsService points,
    TimeProvider time,
    ILogger<TransferService> logger) : ITransferService
{
    private const string FileNamePrefix = "huishoudplanner-";

    private const string RedemptionsWouldBeRemoved = "redemptions_would_be_removed";

    private const string BadgesWouldBeRemoved = "badges_would_be_removed";

    public async Task<OneOf<TransferFile, PortError>> ExportAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var zone = await HouseholdZoneAsync(cancellationToken).ConfigureAwait(false);
        var exported = await data.ExportAsync(now, cancellationToken).ConfigureAwait(false);
        return exported.Match<OneOf<TransferFile, PortError>>(
            content => new TransferFile(
                FileNamePrefix + DayKeys.Today(zone, now).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json",
                "application/json",
                content),
            error => error);
    }

    public async Task<OneOf<ImportResult, ValidationErrors, ConfirmationRequired, ConflictError, PortError>> ImportAsync(
        Actor actor, ImportOptions options, Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(body);

        if (options.Mode != ImportOptions.ReplaceMode)
        {
            return ValidationErrors.For("mode", options.Mode is null ? "required" : "invalid_enum");
        }

        if (options.Confirm != ImportOptions.Yes)
        {
            return new ConfirmationRequired();
        }

        var parsed = await data.ParseAsync(body, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (parsed.TryPickT2(out var parseError, out var rest))
        {
            return parseError;
        }

        if (rest.TryPickT1(out var invalid, out var file))
        {
            return invalid;
        }

        var refusal = await CheckAcknowledgementsAsync(file, options, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal.Value;
        }

        var ran = await transactions.RunAsync(ct => ReplaceInTransactionAsync(actor, file, ct), cancellationToken).ConfigureAwait(false);
        if (!ran.TryPickT0(out var outcome, out var failed))
        {
            return failed.Match<OneOf<ImportResult, ValidationErrors, ConfirmationRequired, ConflictError, PortError>>(conflict => conflict, error => error);
        }

        if (outcome.TryPickT1(out var replaceError, out var result))
        {
            return replaceError;
        }

        // The data is replaced and audited: a rebuild that fails is logged, and the nightly run repeats it.
        await SafeReconcile.RunAsync(points, AuditActor.From(actor), PointsRecomputeTrigger.Import, logger, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The refusal of a file that would remove redemptions or badges without bringing any back and was not acknowledged; the redemptions come first.</summary>
    private async Task<OneOf<ImportResult, ValidationErrors, ConfirmationRequired, ConflictError, PortError>?> CheckAcknowledgementsAsync(
        ParsedImport file, ImportOptions options, CancellationToken cancellationToken)
    {
        var redemptionsAcknowledged = options.AcknowledgeRedemptions == ImportOptions.Yes || !TransferVersions.LosesRedemptions(file.SchemaVersion);
        var badgesAcknowledged = options.AcknowledgeBadges == ImportOptions.Yes || !TransferVersions.LosesBadges(file.SchemaVersion);
        if (redemptionsAcknowledged && badgesAcknowledged)
        {
            return null;
        }

        var counted = await data.CountExistingAsync(cancellationToken).ConfigureAwait(false);
        if (counted.TryPickT1(out var error, out var existing))
        {
            return error;
        }

        if (!redemptionsAcknowledged && existing.Redemptions > 0)
        {
            return new ConflictError(
                RedemptionsWouldBeRemoved,
                "This file is older than version 5 and has no redemptions; importing it removes the existing ones. Add acknowledgeRedemptions=true.",
                new Dictionary<string, object?> { ["count"] = existing.Redemptions });
        }

        if (!badgesAcknowledged && existing.Badges > 0)
        {
            return new ConflictError(
                BadgesWouldBeRemoved,
                "This file is older than version 6 and has no badges; importing it removes the existing ones. Add acknowledgeBadges=true.",
                new Dictionary<string, object?> { ["count"] = existing.Badges });
        }

        return null;
    }

    private async Task<TransactionOutcome<OneOf<ImportResult, PortError>>> ReplaceInTransactionAsync(Actor actor, ParsedImport file, CancellationToken cancellationToken)
    {
        var replaced = await data.ReplaceAsync(file, cancellationToken).ConfigureAwait(false);
        if (replaced.TryPickT1(out var replaceError, out var result))
        {
            return TransactionOutcome.Abort<OneOf<ImportResult, PortError>>(replaceError);
        }

        var entry = ImportAudit.ForImport(AuditActor.From(actor), file, result, Guid.NewGuid().ToString("N")[..24]);
        var recorded = await audit.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<ImportResult, PortError>>(result),
            error => TransactionOutcome.Abort<OneOf<ImportResult, PortError>>(error));
    }

    /// <summary>The timezone of the settings; the configured one when there are no settings yet or they cannot be read (an export must work to get the data out).</summary>
    private async Task<TimeZoneInfo> HouseholdZoneAsync(CancellationToken cancellationToken)
    {
        var read = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var timezone = read.TryPickT0(out var found, out _) ? found.Timezone : household.Timezone;
        try
        {
            return DayKeys.FindZone(timezone);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DayKeys.FindZone(household.Timezone);
        }
    }
}
