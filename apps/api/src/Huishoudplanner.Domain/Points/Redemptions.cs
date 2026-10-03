using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Points;

/// <summary>
/// A booking request as the API receives it (<c>createRedemptionInputSchema</c> of the Node server). <paramref name="PersonId"/> is
/// <see langword="null"/> for the actor; the note is untrimmed here, the use case trims it.
/// </summary>
public sealed record RedemptionCommand(string? PersonId, int Points, string? Note, string? RequestId);

/// <summary>A redemption ready to be stored: a booked ledger entry, with the day and the conversion factor of its own moment (requirements 4.12).</summary>
public sealed record NewRedemption(
    string PersonId,
    int Points,
    string? Note,
    DateTimeOffset Date,
    DateTimeOffset WeekStart,
    int CentsPerPoint,
    string CurrencyCode,
    string? RequestId,
    DateTimeOffset At);

/// <summary>The answer of a booking: the entry, and whether this request created it (<see langword="false"/> when a repeated request key replayed it).</summary>
public sealed record BookedRedemption(PointEntryView View, bool Created);

/// <summary>The rules of booking and undoing a redemption (requirements 4.12, ADR-0011); <c>domain/redemptions.ts</c> of the Node server.</summary>
public static class RedemptionRules
{
    /// <summary>The longest note in characters, counted after trimming (<c>MAX_REDEMPTION_NOTE_LENGTH</c>).</summary>
    public const int MaxNoteLength = 200;

    public const string InsufficientBalance = "insufficient_balance";

    public const string IdempotencyKeyConflict = "idempotency_key_conflict";

    public const string RedemptionLocked = "redemption_locked";

    /// <summary>The shape of a request; <see langword="null"/> when it is valid. Reads nothing.</summary>
    public static ValidationErrors? Validate(RedemptionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (command.PersonId is not null && !OccurrenceRules.IsId(command.PersonId))
        {
            errors["personId"] = ["invalid_object_id"];
        }

        if (command.Points < 1)
        {
            errors["points"] = ["must be a whole number of at least 1"];
        }

        if (NormalizeNote(command.Note) is { Length: > MaxNoteLength })
        {
            errors["note"] = [$"must be at most {MaxNoteLength} characters"];
        }

        if (command.RequestId is not null && !AdhocRules.IsRequestKey(command.RequestId))
        {
            errors["requestId"] = [AdhocRules.InvalidRequestKey];
        }

        return errors.Count == 0 ? null : new ValidationErrors(errors);
    }

    /// <summary>The note as it is stored: trimmed, and <see langword="null"/> when nothing is left.</summary>
    public static string? NormalizeNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    /// <summary>A repeated request key replays the stored booking only for the same person, points and note; anything else is a conflict.</summary>
    public static bool IsReplay(PointEntry stored, string personId, int points, string? note)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return string.Equals(stored.PersonId, personId, StringComparison.Ordinal) &&
            stored.Amount == -points &&
            string.Equals(stored.Note, note, StringComparison.Ordinal);
    }

    public static ConflictError KeyConflict() =>
        new(IdempotencyKeyConflict, "This request key was already used for a different request");

    public static ConflictError Insufficient(long balance, int requested) =>
        new(
            InsufficientBalance,
            "The balance is too low for this redemption",
            new Dictionary<string, object?> { ["balance"] = balance, ["requested"] = requested });

    /// <summary>The booking of a person other than the actor is an administrator's.</summary>
    public static Forbidden? CheckMayBook(Actor actor, string personId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return string.Equals(actor.ActorId, personId, StringComparison.OrdinalIgnoreCase) || actor.Role == Role.Admin
            ? null
            : new Forbidden("Only an administrator can book a redemption for someone else");
    }

    /// <summary>
    /// An administrator undoes a redemption at any time; the person it belongs to only on the day it was booked, so a settled payout is not undone
    /// silently later. Anybody else is refused.
    /// </summary>
    public static Forbidden? CheckMayUndo(Actor actor, PointEntry redemption, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(redemption);
        if (actor.Role == Role.Admin)
        {
            return null;
        }

        if (!string.Equals(redemption.PersonId, actor.ActorId, StringComparison.OrdinalIgnoreCase))
        {
            return new Forbidden("Only the person it belongs to or an administrator can undo a redemption");
        }

        return DayKeys.ToDayKey(redemption.Date, zone) == DayKeys.ToDayKey(now, zone)
            ? null
            : new Forbidden("A redemption can only be undone on the day it was booked, or by an administrator", RedemptionLocked);
    }

    /// <summary>Today in the household timezone, dated like the Node server: the day and the Monday of its week.</summary>
    public static NewRedemption Draft(RedemptionCommand command, string personId, HouseholdSettings settings, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(settings);
        var today = DayKeys.ToDayKey(now, zone);
        return new NewRedemption(
            personId,
            command.Points,
            NormalizeNote(command.Note),
            DayKeys.FromDayKey(today, zone),
            DayKeys.FromDayKey(DayKeys.MondayOf(today), zone),
            settings.CentsPerPoint ?? 0,
            settings.CurrencyCode ?? SettingsDefaults.CurrencyCode,
            command.RequestId,
            now);
    }
}

/// <summary>
/// How a redemption appears in the audit log, as the Node server writes it (<c>insertRedemption</c> and <c>deleteRedemption</c>): a booking is a
/// <c>points</c> <c>create</c> with <c>meta.reason</c> <c>redemption</c>, an undo a <c>points</c> <c>delete</c> with <c>redemption_undone</c> that
/// keeps the removed fields. The request key is retry bookkeeping and is never part of an entry.
/// </summary>
public static class RedemptionAudit
{
    /// <summary>The fields of a redemption as an audit diff sees them; the note is always present (null without one), like the stored document.</summary>
    public static AuditObject Fields(PointEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var properties = new List<KeyValuePair<string, AuditValue>>
        {
            new("key", entry.Key),
            new("kind", PointNames.ToWire(entry.Kind)),
            new("personId", new AuditObjectId(entry.PersonId)),
            new("amount", entry.Amount),
            new("date", entry.Date),
            new("weekStart", entry.WeekStart),
            new("periodStart", AuditNull.Instance),
            new("occurrenceId", AuditNull.Instance),
            new("taskId", AuditNull.Instance),
            new("titleSnapshot", entry.TitleSnapshot),
            new("source", PointNames.ToWire(entry.Source)),
            new("note", entry.Note is { } note ? (AuditValue)note : AuditNull.Instance),
        };
        if (entry.CentsPerPointSnapshot is { } cents)
        {
            properties.Add(new("centsPerPointSnapshot", cents));
        }

        if (entry.CurrencyCodeSnapshot is { } currency)
        {
            properties.Add(new("currencyCodeSnapshot", currency));
        }

        return new AuditObject(properties);
    }

    public static AuditEntry ForBooked(AuditActor actor, PointEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ChangeSet.Between(null, Fields(entry), Ignore).ToEntry(actor, AuditEntity.Points, entry.Id, AuditAction.Create, Meta("redemption"));
    }

    public static AuditEntry ForUndone(AuditActor actor, PointEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ChangeSet.Between(Fields(entry), null, Ignore).ToEntry(actor, AuditEntity.Points, entry.Id, AuditAction.Delete, Meta("redemption_undone"));
    }

    private static IReadOnlyList<string> Ignore { get; } = ["updatedAt", "createdAt"];

    private static AuditObject Meta(string reason) => AuditObject.Of(("reason", reason));
}
