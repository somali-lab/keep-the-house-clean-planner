using System.Globalization;
using Huishoudplanner.Domain.Audit;

namespace Huishoudplanner.Adapters.Http.Audit;

/// <summary>
/// One entry of the history. <see cref="Before"/> and <see cref="After"/> hold the changed fields only (JSON as stored: ids
/// and instants as strings); <see cref="Meta"/> is <c>null</c> when the entry has none (the Node server leaves it out). <see cref="Entity"/>, <see cref="Action"/>
/// and <see cref="Source"/> are the stored names; <c>ai-apply</c> can still appear as an action in old history.
/// </summary>
public sealed record AuditEntryResponse(
    string Id,
    DateTimeOffset At,
    string ActorId,
    string Entity,
    string EntityId,
    string Action,
    IReadOnlyDictionary<string, object?> Before,
    IReadOnlyDictionary<string, object?> After,
    string Source,
    IReadOnlyDictionary<string, object?>? Meta)
{
    internal static AuditEntryResponse From(AuditLogEntry entry) => new(
        entry.Id,
        entry.At,
        entry.ActorId,
        entry.Entity,
        entry.EntityId,
        entry.Action,
        Plain(entry.Before),
        Plain(entry.After),
        entry.Source,
        entry.Meta is null ? null : Plain(entry.Meta));

    private static Dictionary<string, object?> Plain(AuditObject value) =>
        value.Properties.ToDictionary(p => p.Key, p => Plain(p.Value), StringComparer.Ordinal);

    /// <summary>The JSON value of an audit value: the type names of the stored form are gone, instants are ISO strings, ids hex strings.</summary>
    private static object? Plain(AuditValue value) => value switch
    {
        AuditNull => null,
        AuditBool b => b.Value,
        AuditString s => s.Value,
        AuditInteger i => i.Value,
        AuditDouble d => double.IsFinite(d.Value) ? d.Value : null,
        AuditInstant t => t.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        AuditObjectId id => id.Hex,
        AuditArray a => a.Items.Select(Plain).ToList(),
        AuditObject o => Plain(o),
        _ => null,
    };
}

/// <summary>One page of the history, newest first; <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record AuditListResponse(IReadOnlyList<AuditEntryResponse> Items, string? NextCursor);

/// <summary>The answer to clearing the history: the number of entries that were removed.</summary>
public sealed record AuditClearedResponse(int Deleted);
