using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Huishoudplanner.Domain.Audit;

/// <summary>
/// A stored audit entry as the history shows it (read model; the write side is <see cref="AuditEntry"/>). The entity, action
/// and source are the wire names of <see cref="AuditNames"/> as stored, so one unexpected value in an old entry never
/// makes the history unreadable. <see cref="Before"/> and <see cref="After"/> hold the changed fields only.
/// </summary>
public sealed record AuditLogEntry(
    string Id,
    DateTimeOffset At,
    string ActorId,
    string Entity,
    string EntityId,
    string Action,
    AuditObject Before,
    AuditObject After,
    string Source,
    AuditObject? Meta);

/// <summary>What <c>GET /audit</c> filters on; every part is optional and the parts combine with and. <c>From</c> and <c>To</c> are inclusive.</summary>
public sealed record AuditLogFilter(
    AuditEntity? Entity = null,
    string? EntityId = null,
    string? ActorId = null,
    AuditSource? Source = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

/// <summary>One page of the history, newest first. <see cref="NextCursor"/> is <see langword="null"/> on the last page.</summary>
public sealed record AuditLogPage(IReadOnlyList<AuditLogEntry> Items, string? NextCursor)
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;
}

/// <summary>
/// The position after an entry in the order (<c>at</c> descending, then id descending). The encoding is the one of the Node
/// server (base64url of <c>"{ISO instant}|{id}"</c>), so a cursor of either application works on the other during the parallel run.
/// </summary>
public sealed record AuditCursor(DateTimeOffset At, string Id)
{
    public static AuditCursor After(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(entry.At, entry.Id);
    }

    public string Encode() => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
        At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "|" + Id.ToLowerInvariant()));

    /// <summary>False for anything this application did not produce.</summary>
    public static bool TryDecode(string? value, out AuditCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length < 2 || parts[0].Length == 0 || !AuditObjectId.IsHex(parts[1]) ||
            !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            return false;
        }

        cursor = new AuditCursor(at, parts[1].ToLowerInvariant());
        return true;
    }
}

/// <summary>What the occurrence of an old occurrence entry looked like, to show the entry without a second lookup (requirements 4.9).</summary>
public sealed record OccurrenceContext(string TaskName, string? RoomName, DateTimeOffset Date);

/// <summary>The outcome of the retention job when audit retention is not configured: nothing was deleted.</summary>
public readonly record struct RetentionDisabled;

/// <summary>The outcome of a retention run: entries older than <see cref="Cutoff"/> were deleted, <see cref="Deleted"/> of them.</summary>
public readonly record struct RetentionDone(DateTimeOffset Cutoff, int Deleted);

/// <summary>How long audit entries are kept; <see cref="Days"/> is <see langword="null"/> when they are kept indefinitely.</summary>
public sealed record AuditRetentionPolicy(int? Days);
