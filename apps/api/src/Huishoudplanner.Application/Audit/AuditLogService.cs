using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using OneOf;

namespace Huishoudplanner.Application.Audit;

/// <summary>
/// Reads and clears the history (requirements 4.9). Port of <c>routes/audit.ts</c>. Clearing is deliberately not audited:
/// recording it would immediately make a history that was emptied on request non-empty again.
/// </summary>
public sealed class AuditLogService(
    ForReadingAuditLog log,
    ForReadingOccurrenceContext occurrences,
    ForDeletingAuditEntries eraser) : IAuditLogService
{
    public async Task<OneOf<AuditLogPage, ValidationErrors, PortError>> ListAsync(AuditLogFilter filter, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (filter.EntityId is not null && !AuditObjectId.IsHex(filter.EntityId))
        {
            errors["entityId"] = ["invalid_object_id"];
        }

        if (filter.ActorId is not null && !AuditObjectId.IsHex(filter.ActorId))
        {
            errors["actorId"] = ["invalid_object_id"];
        }

        var take = limit ?? AuditLogPage.DefaultLimit;
        if (take is < 1 or > AuditLogPage.MaxLimit)
        {
            errors["limit"] = ["must be a whole number from 1 to " + AuditLogPage.MaxLimit];
        }

        AuditCursor? after = null;
        if (cursor is not null)
        {
            if (AuditCursor.TryDecode(cursor, out var decoded))
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

        var found = await log.ListAsync(Normalized(filter), after, take + 1, cancellationToken).ConfigureAwait(false);
        if (found.TryPickT1(out var error, out var entries))
        {
            return error;
        }

        var page = entries.Take(take).ToList();
        var enriched = await EnrichAsync(page, cancellationToken).ConfigureAwait(false);
        if (enriched.TryPickT1(out var enrichError, out var items))
        {
            return enrichError;
        }

        var nextCursor = entries.Count > take ? AuditCursor.After(page[^1]).Encode() : null;
        return new AuditLogPage(items, nextCursor);
    }

    public async Task<OneOf<int, PortError>> ClearAsync(CancellationToken cancellationToken) =>
        await eraser.ClearAsync(cancellationToken).ConfigureAwait(false);

    private static AuditLogFilter Normalized(AuditLogFilter filter) => filter with
    {
        EntityId = filter.EntityId?.ToLowerInvariant(),
        ActorId = filter.ActorId?.ToLowerInvariant(),
    };

    /// <summary>
    /// Entries of occurrences from before the entry carried its own context get the task, room and date of their occurrence
    /// (the occurrence keeps snapshots, so this still works after the task or room is gone).
    /// </summary>
    private async Task<OneOf<IReadOnlyList<AuditLogEntry>, PortError>> EnrichAsync(List<AuditLogEntry> page, CancellationToken cancellationToken)
    {
        var wanted = page.Where(NeedsOccurrenceContext).Select(e => e.EntityId).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return page;
        }

        var found = await occurrences.FindAsync(wanted, cancellationToken).ConfigureAwait(false);
        if (found.TryPickT1(out var error, out var contexts))
        {
            return error;
        }

        return page.ConvertAll(entry =>
            NeedsOccurrenceContext(entry) && contexts.TryGetValue(entry.EntityId, out var context)
                ? entry with { Meta = WithOccurrence(entry.Meta, context) }
                : entry);
    }

    private static bool NeedsOccurrenceContext(AuditLogEntry entry) =>
        entry.Entity == AuditNames.ToWire(AuditEntity.Occurrence) && entry.Meta?["occurrence"] is null or AuditNull;

    private static AuditObject WithOccurrence(AuditObject? meta, OccurrenceContext context)
    {
        var occurrence = AuditObject.Of(
            ("taskNameSnapshot", context.TaskName),
            ("roomNameSnapshot", context.RoomName is null ? AuditNull.Instance : (AuditValue)new AuditString(context.RoomName)),
            ("date", context.Date));
        return new AuditObject([.. meta?.Properties ?? [], new KeyValuePair<string, AuditValue>("occurrence", occurrence)]);
    }
}
