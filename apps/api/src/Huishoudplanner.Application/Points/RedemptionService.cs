using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using OneOf;

namespace Huishoudplanner.Application.Points;

/// <summary>
/// Booking and undoing redemptions (requirements 4.12, ADR-0011); port of <c>domain/redemptions.ts</c>. A redemption is a booked ledger entry, never
/// derived and never touched by the reconciliation. Every write is one transaction with its audit entry; a refusal and a replay write and audit nothing.
/// </summary>
/// <remarks>
/// <para>The Node server serialised bookings in one in-process queue. Here the balance check and the insert run in one transaction, and the first
/// write of a booking is the guard of the person (<see cref="ForStoringRedemptions.LockBalanceAsync"/>): snapshot isolation allows write skew, so
/// two bookings of one person that both read the same balance would both commit; with the guard they write the same document, one of them
/// conflicts, the transaction runner reruns it and its balance read then sees the other's entry (mongodb-persistence rule 8).</para>
/// <para>Idempotency: the request key is looked up first, so a repeat replays the stored booking whatever the balance is now. Two requests with one
/// key at once both find nothing and both insert; the unique index lets exactly one commit, the loser's attempt is rolled back and rerun and its second
/// lookup finds the winner (the pattern of the ad-hoc occurrences).</para>
/// </remarks>
public sealed class RedemptionService(
    ForStoringRedemptions redemptions,
    ForStoringSettings settings,
    ForStoringUsers users,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IRedemptionService
{
    /// <summary>A lost race for a request key is settled by the winner's record; a handful of attempts is far more than one race needs.</summary>
    private const int MaxAttempts = 3;

    /// <summary>The outcome of one attempt: the booking, or <see langword="null"/> when the insert lost the race for its request key.</summary>
    private readonly record struct Attempt(BookedRedemption? Booked);

    public async Task<OneOf<BookedRedemption, ValidationErrors, Forbidden, ConflictError, SettingsMissing, PortError>> BookAsync(
        Actor actor, RedemptionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        if (RedemptionRules.Validate(command) is { } invalid)
        {
            return invalid;
        }

        var personId = (command.PersonId ?? actor.ActorId).ToLowerInvariant();
        if (RedemptionRules.CheckMayBook(actor, personId) is { } forbidden)
        {
            return forbidden;
        }

        var normalised = command with { PersonId = personId, Note = RedemptionRules.NormalizeNote(command.Note) };
        var auditActor = AuditActor.From(actor);
        for (var number = 1; ; number++)
        {
            var ran = await transactions.RunAsync(
                async ct =>
                {
                    var step = await BookCoreAsync(auditActor, normalised, ct).ConfigureAwait(false);
                    return step.IsT0 && step.AsT0.Booked is not null ? TransactionOutcome.Commit(step) : TransactionOutcome.Abort(step);
                },
                cancellationToken).ConfigureAwait(false);
            if (!ran.TryPickT0(out var outcome, out var rest))
            {
                return rest.Match<OneOf<BookedRedemption, ValidationErrors, Forbidden, ConflictError, SettingsMissing, PortError>>(conflict => conflict, error => error);
            }

            if (outcome.TryPickT0(out var attempt, out var refusal))
            {
                if (attempt.Booked is { } booked)
                {
                    return booked;
                }

                if (number >= MaxAttempts)
                {
                    // The key is held by a record nobody can see (removed in between again and again): the same answer as the Node server.
                    return RedemptionRules.KeyConflict();
                }

                continue;
            }

            return refusal.Match<OneOf<BookedRedemption, ValidationErrors, Forbidden, ConflictError, SettingsMissing, PortError>>(
                invalid => invalid, conflict => conflict, missing => missing, error => error);
        }
    }

    private async Task<OneOf<Attempt, ValidationErrors, ConflictError, SettingsMissing, PortError>> BookCoreAsync(
        AuditActor actor, RedemptionCommand command, CancellationToken ct)
    {
        var personId = command.PersonId!;
        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        if (!read.TryPickT0(out var household, out var readFailure))
        {
            return readFailure.Match<OneOf<Attempt, ValidationErrors, ConflictError, SettingsMissing, PortError>>(missing => missing, error => error);
        }

        var zone = DayKeys.FindZone(household.Timezone);
        if (command.RequestId is { } key)
        {
            var found = await redemptions.FindByRequestIdAsync(key, ct).ConfigureAwait(false);
            if (found.TryPickT2(out var findFailure, out var foundRest))
            {
                return findFailure;
            }

            if (foundRest.TryPickT0(out var stored, out _))
            {
                return RedemptionRules.IsReplay(stored, personId, command.Points, command.Note)
                    ? new Attempt(new BookedRedemption(PointEntryView.From(stored, zone), false))
                    : RedemptionRules.KeyConflict();
            }
        }

        var person = await users.FindAsync(personId, ct).ConfigureAwait(false);
        if (person.TryPickT2(out var personFailure, out var personRest))
        {
            return personFailure;
        }

        if (!personRest.TryPickT0(out var user, out _) || !user.Active)
        {
            return ValidationErrors.For("personId", personRest.IsT0 ? "inactive_user" : "unknown_user");
        }

        // The guard first, then the balance: a booking that overlaps this one conflicts here instead of both reading the same balance.
        var locked = await redemptions.LockBalanceAsync(personId, ct).ConfigureAwait(false);
        if (locked.TryPickT1(out var lockFailure, out _))
        {
            return lockFailure;
        }

        var balance = await redemptions.BalanceOfAsync(personId, ct).ConfigureAwait(false);
        if (!balance.TryPickT0(out var available, out var balanceFailure))
        {
            return balanceFailure;
        }

        if (command.Points > available)
        {
            return RedemptionRules.Insufficient(available, command.Points);
        }

        var now = Now();
        var inserted = await redemptions.InsertAsync(RedemptionRules.Draft(command, personId, household, zone, now), ct).ConfigureAwait(false);
        if (inserted.TryPickT2(out var insertFailure, out var insertedRest))
        {
            return insertFailure;
        }

        if (insertedRest.IsT1)
        {
            return new Attempt(null);
        }

        var entry = insertedRest.AsT0;
        var recorded = await audit.RecordAsync(RedemptionAudit.ForBooked(actor, entry), ct).ConfigureAwait(false);
        if (recorded.TryPickT1(out var auditFailure, out _))
        {
            return auditFailure;
        }

        return new Attempt(new BookedRedemption(PointEntryView.From(entry, zone), true));
    }

    public async Task<OneOf<Success, NotFound, ValidationErrors, Forbidden, ConflictError, SettingsMissing, PortError>> UndoAsync(
        Actor actor, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!OccurrenceRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var auditActor = AuditActor.From(actor);
        var ran = await transactions.RunAsync(
            async ct =>
            {
                var step = await UndoCoreAsync(actor, auditActor, id.ToLowerInvariant(), ct).ConfigureAwait(false);
                return step.IsT0 ? TransactionOutcome.Commit(step) : TransactionOutcome.Abort(step);
            },
            cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<Success, NotFound, ValidationErrors, Forbidden, ConflictError, SettingsMissing, PortError>>(
            step => step.Match<OneOf<Success, NotFound, ValidationErrors, Forbidden, ConflictError, SettingsMissing, PortError>>(
                success => success, notFound => notFound, forbidden => forbidden, missing => missing, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<OneOf<Success, NotFound, Forbidden, SettingsMissing, PortError>> UndoCoreAsync(Actor actor, AuditActor auditActor, string id, CancellationToken ct)
    {
        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        if (!read.TryPickT0(out var household, out var readFailure))
        {
            return readFailure.Match<OneOf<Success, NotFound, Forbidden, SettingsMissing, PortError>>(missing => missing, error => error);
        }

        var found = await redemptions.FindAsync(id, ct).ConfigureAwait(false);
        if (!found.TryPickT0(out var current, out var findRest))
        {
            return findRest.Match<OneOf<Success, NotFound, Forbidden, SettingsMissing, PortError>>(notFound => notFound, error => error);
        }

        if (RedemptionRules.CheckMayUndo(actor, current, DayKeys.FindZone(household.Timezone), Now()) is { } forbidden)
        {
            return forbidden;
        }

        // A concurrent undo already removed it: the same answer as a second undo.
        var deleted = await redemptions.DeleteAsync(id, ct).ConfigureAwait(false);
        if (!deleted.TryPickT0(out var removed, out var deleteRest))
        {
            return deleteRest.Match<OneOf<Success, NotFound, Forbidden, SettingsMissing, PortError>>(notFound => notFound, error => error);
        }

        var recorded = await audit.RecordAsync(RedemptionAudit.ForUndone(auditActor, removed), ct).ConfigureAwait(false);
        return recorded.Match<OneOf<Success, NotFound, Forbidden, SettingsMissing, PortError>>(success => success, error => error);
    }

    public Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken) => redemptions.CountAsync(cancellationToken);

    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
}
