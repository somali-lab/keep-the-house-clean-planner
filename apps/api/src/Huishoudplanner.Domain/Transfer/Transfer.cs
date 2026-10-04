using Huishoudplanner.Domain.Audit;

namespace Huishoudplanner.Domain.Transfer;

/// <summary>
/// The versions of the export file (requirements 8; <c>EXPORT_SCHEMA_VERSION</c> of the Node server). Version 2 added recorded work and request keys, 3 the
/// task points, 4 the bonus schedule, 5 the redemptions and the currency settings, 6 the badge definitions with their images. An import accepts versions 1
/// to <see cref="Current"/>; a file of an older version has no redemptions or badges, so those the import replaces are lost.
/// </summary>
public static class TransferVersions
{
    public const int Current = 6;

    public const int Oldest = 1;

    /// <summary>The first version that carries the redemptions in <c>collections.pointEntries</c>.</summary>
    public const int FirstWithRedemptions = 5;

    /// <summary>The first version that carries the badge definitions in <c>collections.badges</c>.</summary>
    public const int FirstWithBadges = 6;

    /// <summary>Importing a file of this version removes the redemptions that exist, without putting any back (requirements 4.12).</summary>
    public static bool LosesRedemptions(int schemaVersion) => schemaVersion < FirstWithRedemptions;

    /// <summary>Importing a file of this version removes the badges that exist, without putting any back (ADR-0014).</summary>
    public static bool LosesBadges(int schemaVersion) => schemaVersion < FirstWithBadges;
}

/// <summary>A JSON export ready to send: the file name names the day of the export in the household timezone.</summary>
public sealed record TransferFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// The query of an import as the client sent it (null when absent). <see cref="Mode"/> must be <c>replace</c>; the import only proceeds with
/// <c>confirm=true</c>, and a file that would remove redemptions or badges also needs the matching acknowledgement to be <c>true</c>.
/// </summary>
public sealed record ImportOptions(string? Mode, string? Confirm, string? AcknowledgeRedemptions, string? AcknowledgeBadges)
{
    public const string ReplaceMode = "replace";

    public const string Yes = "true";
}

/// <summary>
/// A file that passed every check and is ready to replace the data. The content is held by the adapter that read it (it is made of its own
/// document type); the domain only sees what the rules need.
/// </summary>
public abstract class ParsedImport(int schemaVersion, string exportedAt)
{
    public int SchemaVersion { get; } = schemaVersion;

    /// <summary>The <c>exportedAt</c> of the file, as written in it.</summary>
    public string ExportedAt { get; } = exportedAt;
}

/// <summary>How many documents each replaced collection holds now. The audit log is merged, not replaced, and is counted in <see cref="ImportResult.AuditAdded"/>.</summary>
public sealed record ReplacedCounts(int Settings, int Users, int Rooms, int Tasks, int CyclePlans, int Cycles, int Occurrences, int PointEntries, int Badges);

/// <summary>What an import did: the answer of <c>POST /import/json</c> and the content of its audit entry.</summary>
/// <param name="Replaced">The documents of the file, per collection (the point entries are the redemptions).</param>
/// <param name="AuditAdded">Audit entries of the file that were not in the log yet.</param>
/// <param name="RemovedPointEntries">Entries of the points ledger that were dropped; the rebuild brings the derived ones back.</param>
/// <param name="RemovedRedemptions">The redemptions among them; a file older than version 5 has none to put back.</param>
/// <param name="RemovedBadges">Badges that existed before the import; a file older than version 6 has none to put back.</param>
/// <param name="RemovedBadgeAwards">Badge awards that were dropped; they are derived and rebuilt.</param>
public sealed record ImportResult(
    ReplacedCounts Replaced,
    int AuditAdded,
    int RemovedPointEntries,
    int RemovedRedemptions,
    int RemovedBadges,
    int RemovedBadgeAwards);

/// <summary>What exists now and an older file would remove without bringing back.</summary>
public sealed record ExistingCounts(int Redemptions, int Badges);

/// <summary>An import without <c>confirm=true</c>. Maps to <c>400 confirmation_required</c>.</summary>
public readonly record struct ConfirmationRequired;

/// <summary>The audit entry of an import (requirements 4.9): one entry for the whole replacement, in the same transaction as the writes.</summary>
public static class ImportAudit
{
    public static AuditEntry ForImport(AuditActor actor, ParsedImport parsed, ImportResult result, string entityId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(result);
        var replaced = result.Replaced;
        return new AuditEntry(
            actor,
            AuditEntity.Import,
            entityId,
            AuditAction.Create,
            AuditObject.Empty,
            AuditObject.Of(
                ("settings", replaced.Settings),
                ("users", replaced.Users),
                ("rooms", replaced.Rooms),
                ("tasks", replaced.Tasks),
                ("cyclePlans", replaced.CyclePlans),
                ("cycles", replaced.Cycles),
                ("occurrences", replaced.Occurrences),
                ("pointEntries", replaced.PointEntries),
                ("badges", replaced.Badges),
                ("auditAdded", result.AuditAdded),
                ("removedPointEntries", result.RemovedPointEntries),
                ("removedRedemptions", result.RemovedRedemptions),
                ("removedBadges", result.RemovedBadges),
                ("removedBadgeAwards", result.RemovedBadgeAwards)),
            AuditObject.Of(
                ("mode", ImportOptions.ReplaceMode),
                ("schemaVersion", parsed.SchemaVersion),
                ("exportedAt", parsed.ExportedAt)));
    }
}
