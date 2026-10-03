using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Planning;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using OneOf;

namespace Huishoudplanner.Application.CyclePlans;

/// <summary>
/// The cycle plan use cases (requirements 4.3). Every state change runs in one transaction together with its audit entry; a change that
/// changes nothing writes and audits nothing (ADR-0004). Port of <c>routes/cyclePlans.ts</c>, <c>domain/plans.ts</c> and
/// <c>data/cyclePlans.ts</c> without activation (slice 2.4). Saving the slots of the active plan synchronises the upcoming occurrences in the same transaction.
/// </summary>
public sealed class CyclePlanService(
    ForStoringCyclePlans plans,
    ForStoringTasks tasks,
    ForStoringUsers users,
    ForStoringRooms rooms,
    ForStoringSettings settings,
    IGenerationService generation,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : ICyclePlanService
{
    private readonly PlanReferenceReader references = new(tasks, users, rooms, settings);

    public async Task<OneOf<CyclePlanList, ValidationErrors, PortError>> ListAsync(int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var take = limit ?? CyclePlanListQuery.DefaultLimit;
        if (take is < 1 or > CyclePlanListQuery.MaxLimit)
        {
            errors["limit"] = ["must be a whole number from 1 to " + CyclePlanListQuery.MaxLimit];
        }

        CyclePlanCursor? after = null;
        if (cursor is not null)
        {
            if (CyclePlanCursor.TryDecode(cursor, out var decoded))
            {
                after = decoded;
            }
            else
            {
                errors["cursor"] = ["invalid_cursor"];
            }
        }

        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var found = await plans.ListAsync(after, take + 1, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<CyclePlanList, ValidationErrors, PortError>>(
            items => items.Count > take
                ? new CyclePlanList(items.Take(take).ToList(), CyclePlanCursor.After(items[take - 1]).Encode())
                : new CyclePlanList(items, null),
            error => error);
    }

    public async Task<OneOf<CyclePlan, NotFound, ValidationErrors, PortError>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!CyclePlanRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var found = await plans.FindAsync(id.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<CyclePlan, NotFound, ValidationErrors, PortError>>(plan => plan, notFound => notFound, error => error);
    }

    public Task<OneOf<CyclePlan, NotFound, PortError>> GetActiveAsync(CancellationToken cancellationToken) =>
        plans.FindActiveAsync(cancellationToken);

    // ---- create

    public async Task<OneOf<CyclePlan, NotFound, ValidationErrors, ConflictError, PortError>> CreateAsync(Actor actor, CreateCyclePlanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        if (CyclePlanRules.Validate(command) is { } invalid)
        {
            return invalid;
        }

        var normalised = new CreateCyclePlanCommand(command.Name.Trim(), command.CopyFromId?.ToLowerInvariant());
        var ran = await transactions.RunAsync(ct => CreateInTransactionAsync(actor, normalised, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<CyclePlan, NotFound, ValidationErrors, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<CyclePlan, NotFound, ValidationErrors, ConflictError, PortError>>(plan => plan, notFound => notFound, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<CyclePlan, NotFound, PortError>>> CreateInTransactionAsync(Actor actor, CreateCyclePlanCommand command, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<CyclePlan, NotFound, PortError>> Abort(OneOf<CyclePlan, NotFound, PortError> value) => TransactionOutcome.Abort(value);

        CyclePlan? source = null;
        if (command.CopyFromId is { } copyFrom)
        {
            var found = await plans.FindAsync(copyFrom, ct).ConfigureAwait(false);
            if (found.TryPickT1(out var notFound, out var rest))
            {
                return Abort(notFound);
            }

            if (rest.TryPickT1(out var findError, out var plan))
            {
                return Abort(findError);
            }

            source = plan;
        }

        var inserted = await plans.InsertAsync(
            new NewCyclePlan(command.Name, false, CyclePlanSlots.Sort(source?.Slots ?? []), source?.WeekThemes ?? CyclePlanRules.EmptyWeekThemes, time.GetUtcNow()),
            ct).ConfigureAwait(false);
        if (inserted.TryPickT1(out var insertError, out var created))
        {
            return Abort(insertError);
        }

        var recorded = await audit.RecordAsync(CyclePlanAudit.ForCreate(AuditActor.From(actor), created, source?.Id), ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<CyclePlan, NotFound, PortError>>(created),
            error => Abort(error));
    }

    // ---- update

    public async Task<OneOf<CyclePlan, NotFound, ValidationErrors, ConflictError, PortError>> UpdateAsync(Actor actor, string id, CyclePlanPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(patch);
        if (!CyclePlanRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        if (CyclePlanRules.Validate(patch) is { } invalid)
        {
            return invalid;
        }

        var normalised = patch with { Name = patch.Name?.Trim() };
        var ran = await transactions.RunAsync(ct => UpdateInTransactionAsync(actor, id.ToLowerInvariant(), normalised, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<CyclePlan, NotFound, ValidationErrors, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<CyclePlan, NotFound, ValidationErrors, ConflictError, PortError>>(plan => plan, notFound => notFound, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<CyclePlan, NotFound, PortError>>> UpdateInTransactionAsync(Actor actor, string id, CyclePlanPatch patch, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<CyclePlan, NotFound, PortError>> Abort(OneOf<CyclePlan, NotFound, PortError> value) => TransactionOutcome.Abort(value);

        var found = await plans.FindAsync(id, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var rest))
        {
            return Abort(notFound);
        }

        if (rest.TryPickT1(out var findError, out var before))
        {
            return Abort(findError);
        }

        var after = before with { Name = patch.Name ?? before.Name, WeekThemes = patch.WeekThemes ?? before.WeekThemes };
        var change = ChangeSet.Between(CyclePlanAudit.Fields(before), CyclePlanAudit.Fields(after));
        if (change.IsNoOp)
        {
            return TransactionOutcome.Commit<OneOf<CyclePlan, NotFound, PortError>>(before);
        }

        var changes = new PlanMetaChanges(
            after.Name != before.Name ? after.Name : null,
            after.WeekThemes.SequenceEqual(before.WeekThemes, StringComparer.Ordinal) ? null : after.WeekThemes);
        var updated = await plans.UpdateMetaAsync(id, changes, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (updated.TryPickT1(out var gone, out var updatedRest))
        {
            return Abort(gone);
        }

        if (updatedRest.TryPickT1(out var updateError, out var plan))
        {
            return Abort(updateError);
        }

        var recorded = await audit.RecordAsync(change.ToEntry(AuditActor.From(actor), AuditEntity.CyclePlan, id, AuditAction.Update), ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<CyclePlan, NotFound, PortError>>(plan),
            error => Abort(error));
    }

    // ---- delete

    public async Task<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>> DeleteAsync(Actor actor, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!CyclePlanRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var ran = await transactions.RunAsync(ct => DeleteInTransactionAsync(actor, id.ToLowerInvariant(), ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>>(
                success => success, notFound => notFound, conflict => conflict, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<Success, NotFound, ConflictError, PortError>>> DeleteInTransactionAsync(Actor actor, string id, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<Success, NotFound, ConflictError, PortError>> Abort(OneOf<Success, NotFound, ConflictError, PortError> value) => TransactionOutcome.Abort(value);

        var found = await plans.FindAsync(id, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var rest))
        {
            return Abort(notFound);
        }

        if (rest.TryPickT1(out var findError, out var plan))
        {
            return Abort(findError);
        }

        var oldest = await plans.FindDefaultAsync(ct).ConfigureAwait(false);
        if (oldest.TryPickT2(out var defaultError, out var oldestRest))
        {
            return Abort(defaultError);
        }

        CyclePlan? defaultPlan = oldestRest.Match<CyclePlan?>(p => p, _ => null);
        if (CyclePlanRules.DeleteConflict(plan, defaultPlan) is { } conflict)
        {
            return Abort(conflict);
        }

        var deleted = await plans.DeleteAsync(id, ct).ConfigureAwait(false);
        if (deleted.TryPickT1(out var gone, out var deletedRest))
        {
            return Abort(gone);
        }

        if (deletedRest.TryPickT1(out var deleteError, out _))
        {
            return Abort(deleteError);
        }

        var recorded = await audit.RecordAsync(CyclePlanAudit.ForDelete(AuditActor.From(actor), plan), ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<Success, NotFound, ConflictError, PortError>>(new Success()),
            error => Abort(error));
    }

    // ---- slots

    public async Task<OneOf<PlanSlotsSaved, NotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing>> ReplaceSlotsAsync(
        Actor actor, string id, IReadOnlyList<CyclePlanSlot> slots, CancellationToken cancellationToken, AuditObject? meta = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(slots);
        if (!CyclePlanRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        if (CyclePlanRules.Validate(slots) is { } invalid)
        {
            return invalid;
        }

        var normalised = CyclePlanRules.Normalise(slots);
        var ran = await transactions.RunAsync(ct => ReplaceSlotsInTransactionAsync(actor, id.ToLowerInvariant(), normalised, meta, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<PlanSlotsSaved, NotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing>>(
            outcome => outcome.Match<OneOf<PlanSlotsSaved, NotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing>>(
                saved => saved, notFound => notFound, rejected => rejected, error => error, missing => missing),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<PlanSlotsSaved, NotFound, InvalidPlan, PortError, SettingsMissing>>> ReplaceSlotsInTransactionAsync(
        Actor actor, string id, IReadOnlyList<CyclePlanSlot> slots, AuditObject? meta, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<PlanSlotsSaved, NotFound, InvalidPlan, PortError, SettingsMissing>> Abort(OneOf<PlanSlotsSaved, NotFound, InvalidPlan, PortError, SettingsMissing> value) => TransactionOutcome.Abort(value);

        var found = await plans.FindAsync(id, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var rest))
        {
            return Abort(notFound);
        }

        if (rest.TryPickT1(out var findError, out var before))
        {
            return Abort(findError);
        }

        var read = await references.ReadAsync(ct).ConfigureAwait(false);
        if (read.TryPickT1(out var readError, out var refs))
        {
            return Abort(readError);
        }

        var validation = refs.Validate(slots);
        if (!validation.IsValid)
        {
            return Abort(new InvalidPlan(validation));
        }

        var next = CyclePlanSlots.Sort(slots);
        var diff = CyclePlanSlots.Diff(before.Slots, next);
        var plan = before;
        if (!diff.IsEmpty)
        {
            var replaced = await plans.ReplaceSlotsAsync(id, next, time.GetUtcNow(), ct).ConfigureAwait(false);
            if (replaced.TryPickT1(out var gone, out var replacedRest))
            {
                return Abort(gone);
            }

            if (replacedRest.TryPickT1(out var replaceError, out plan))
            {
                return Abort(replaceError);
            }

            var recorded = await audit.RecordAsync(CyclePlanAudit.ForSlots(AuditActor.From(actor), id, diff, meta), ct).ConfigureAwait(false);
            if (recorded.TryPickT1(out var auditError, out _))
            {
                return Abort(auditError);
            }
        }

        // Saving the slots of the ACTIVE plan always synchronises the upcoming occurrences, in this transaction, whether or not the slots
        // changed (requirements 4.3). The replacement is attributed to the saving profile with the system as source.
        ReplacementResult? synchronized = null;
        if (plan.Active)
        {
            var replacement = await generation.ReplaceUpcomingAsync(
                new AuditActor(actor.ActorId, AuditSource.System), id, GenerationRunIds.New(), ReplacementReasons.PlanUpdate, ct).ConfigureAwait(false);
            if (replacement.TryPickT1(out var vanished, out var replacementRest))
            {
                return Abort(vanished);
            }

            if (replacementRest.TryPickT1(out var missing, out var settingsRest))
            {
                return Abort(missing);
            }

            if (settingsRest.TryPickT1(out var conflict, out var errorRest))
            {
                return Abort(new PortError($"generation.conflict: the synchronisation of the upcoming occurrences ended in a conflict ({conflict.Code})."));
            }

            if (errorRest.TryPickT1(out var syncError, out _))
            {
                return Abort(syncError);
            }

            synchronized = replacement.AsT0;
        }

        return TransactionOutcome.Commit<OneOf<PlanSlotsSaved, NotFound, InvalidPlan, PortError, SettingsMissing>>(
            new PlanSlotsSaved(plan, validation.Warnings, validation.Summary, synchronized));
    }

    // ---- compare and validate

    public async Task<OneOf<PlanComparison, NotFound, ValidationErrors, PortError>> CompareWithActiveAsync(string id, CancellationToken cancellationToken)
    {
        var loaded = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!loaded.TryPickT0(out var plan, out var failure))
        {
            return failure.Match<OneOf<PlanComparison, NotFound, ValidationErrors, PortError>>(notFound => notFound, invalid => invalid, error => error);
        }

        var activeRead = await plans.FindActiveAsync(cancellationToken).ConfigureAwait(false);
        if (activeRead.TryPickT2(out var activeError, out var activeRest))
        {
            return activeError;
        }

        CyclePlan? active = activeRest.Match<CyclePlan?>(p => p, _ => null);
        var info = await references.ReadTaskInfoAsync(cancellationToken).ConfigureAwait(false);
        if (info.TryPickT1(out var infoError, out var taskInfo))
        {
            return infoError;
        }

        var read = await references.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.TryPickT1(out var readError, out var refs))
        {
            return readError;
        }

        IReadOnlyList<CyclePlanSlot> baseSlots = active?.Slots ?? [];
        var before = refs.Validate(baseSlots);
        var after = refs.Validate(plan.Slots);
        return new PlanComparison(plan.Id, active?.Id, PlanDiffer.Diff(baseSlots, plan.Slots, taskInfo), before.Summary.Weeks, after.Summary.Weeks, after.Warnings);
    }

    public async Task<OneOf<PlanValidation, NotFound, ValidationErrors, PortError>> ValidateAsync(string id, CancellationToken cancellationToken)
    {
        var loaded = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!loaded.TryPickT0(out var plan, out var failure))
        {
            return failure.Match<OneOf<PlanValidation, NotFound, ValidationErrors, PortError>>(notFound => notFound, invalid => invalid, error => error);
        }

        var read = await references.ReadAsync(cancellationToken).ConfigureAwait(false);
        return read.Match<OneOf<PlanValidation, NotFound, ValidationErrors, PortError>>(refs => refs.Validate(plan.Slots), error => error);
    }

    public async Task<OneOf<PlanValidation, ValidationErrors, PortError>> ValidateDraftAsync(IReadOnlyList<CyclePlanSlot> slots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (CyclePlanRules.Validate(slots) is { } invalid)
        {
            return invalid;
        }

        var normalised = CyclePlanRules.Normalise(slots);
        var read = await references.ReadAsync(cancellationToken).ConfigureAwait(false);
        return read.Match<OneOf<PlanValidation, ValidationErrors, PortError>>(refs => refs.Validate(normalised), error => error);
    }
}
