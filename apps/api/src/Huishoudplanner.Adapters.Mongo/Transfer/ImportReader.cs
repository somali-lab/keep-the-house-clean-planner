using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Transfer;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Transfer;

/// <summary>A file that passed every check: the documents per collection with only their known fields, as the stored BSON types.</summary>
internal sealed class MongoParsedImport(int schemaVersion, string exportedAt, IReadOnlyDictionary<string, List<BsonDocument>> documents)
    : ParsedImport(schemaVersion, exportedAt)
{
    public IReadOnlyDictionary<string, List<BsonDocument>> Documents { get; } = documents;
}

/// <summary>
/// Reads an export file and validates all of it before anything is written (<c>parseImport</c> of <c>domain/transfer.ts</c>): the envelope, the shape and
/// storage types of every document (<see cref="ImportSchemas"/>), and the rules between documents that the unique indexes and the household need. A file
/// that fails any of it is refused as a whole, because replacing the data deletes before it inserts.
/// </summary>
internal static class ImportReader
{
    /// <summary>The collections in the order they are listed in a file and replaced on import.</summary>
    public static readonly string[] Order =
    [
        MongoCollections.Settings, MongoCollections.Users, MongoCollections.Rooms, MongoCollections.Tasks, MongoCollections.CyclePlans, MongoCollections.Cycles,
        MongoCollections.Occurrences, MongoCollections.PointEntries, MongoCollections.Badges, MongoCollections.AuditLog,
    ];

    private static readonly string[] AlwaysPresent = [.. Order.Where(n => n is not (MongoCollections.PointEntries or MongoCollections.Badges))];

    public static async Task<OneOf<MongoParsedImport, ValidationErrors>> ReadAsync(Stream body, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var issues = new ImportIssues();
        BsonValue root;

        // The JSON reader is synchronous and the request body is not allowed to be: buffer it, then parse from memory. Reading the body is not guarded:
        // a body over the limit or a connection that breaks is the host's to answer, not an invalid file.
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        try
        {
            using var text = new StreamReader(buffer, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            using var reader = new JsonReader(text);
            root = BsonSerializer.Deserialize<BsonValue>(reader);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            return ValidationErrors.For("body", "invalid_json");
        }

        if (!root.IsBsonDocument)
        {
            return ValidationErrors.For("body", "expected_object");
        }

        var envelope = ReadEnvelope(root.AsBsonDocument, issues);
        if (envelope is null)
        {
            return issues.ToErrors();
        }

        var (version, exportedAt, collections) = envelope.Value;
        var docs = new Dictionary<string, List<BsonDocument>>(StringComparer.Ordinal);
        foreach (var name in Order)
        {
            docs[name] = [];
            if (!collections.TryGetValue(name, out var array))
            {
                continue;
            }

            var shape = ImportSchemas.Collections[name];
            for (var i = 0; i < array.AsBsonArray.Count && !issues.Full; i++)
            {
                var doc = array.AsBsonArray[i];
                shape.Validate(doc, $"collections.{name}.{i}", issues);
                if (doc.IsBsonDocument)
                {
                    docs[name].Add(shape.Keep(doc.AsBsonDocument));
                }
            }
        }

        CheckHousehold(docs, now, issues);

        // The keys are built from typed values, so they are only checked once every document has the right types.
        if (!issues.Any)
        {
            ReconcileBadgeTasks(docs[MongoCollections.Badges], docs[MongoCollections.Tasks]);
            CheckDuplicates(docs, issues);
            CheckRedemptions(docs, issues);
            CheckBadges(docs[MongoCollections.Badges], issues);
        }

        return issues.Any ? issues.ToErrors() : new MongoParsedImport(version, exportedAt, docs);
    }

    private static (int Version, string ExportedAt, Dictionary<string, BsonValue> Collections)? ReadEnvelope(BsonDocument root, ImportIssues issues)
    {
        var version = 0;
        if (!root.TryGetValue("schemaVersion", out var versionValue))
        {
            issues.Add("schemaVersion", "required");
        }
        else if (!Shape.TryInteger(versionValue, out var number) || number is < TransferVersions.Oldest or > TransferVersions.Current)
        {
            issues.Add("schemaVersion", "unsupported_version");
        }
        else
        {
            version = (int)number;
        }

        var exportedAt = string.Empty;
        if (!root.TryGetValue("exportedAt", out var exportedValue))
        {
            issues.Add("exportedAt", "required");
        }
        else if (!exportedValue.IsString || !Shape.IsoInstant().IsMatch(exportedValue.AsString)
            || !DateTimeOffset.TryParse(exportedValue.AsString, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            issues.Add("exportedAt", "invalid_datetime");
        }
        else
        {
            exportedAt = exportedValue.AsString;
        }

        var collections = new Dictionary<string, BsonValue>(StringComparer.Ordinal);
        if (!root.TryGetValue("collections", out var collectionsValue))
        {
            issues.Add("collections", "required");
        }
        else if (!collectionsValue.IsBsonDocument)
        {
            issues.Add("collections", "expected_object");
        }
        else
        {
            foreach (var name in Order)
            {
                var path = $"collections.{name}";
                var optional = name is MongoCollections.PointEntries or MongoCollections.Badges;
                if (!collectionsValue.AsBsonDocument.TryGetValue(name, out var array))
                {
                    // Only from version 5 a file has its redemptions and only from version 6 its badges; leaving them out would silently drop them on import.
                    var needed = name == MongoCollections.PointEntries ? TransferVersions.FirstWithRedemptions : TransferVersions.FirstWithBadges;
                    if (!optional || version >= needed)
                    {
                        issues.Add(path, "required");
                    }
                }
                else if (!array.IsBsonArray)
                {
                    issues.Add(path, "expected_array");
                }
                else
                {
                    collections[name] = array;
                }
            }
        }

        return issues.Any ? null : (version, exportedAt, collections);
    }

    /// <summary>The settings singleton, an active person to act as, and a bonus schedule that only holds what already applies (a later run would otherwise apply a row nobody set).</summary>
    private static void CheckHousehold(Dictionary<string, List<BsonDocument>> docs, DateTimeOffset now, ImportIssues issues)
    {
        var settings = docs[MongoCollections.Settings];
        if (settings.Count != 1 || settings[0].GetValue("_id", BsonNull.Value) != SettingsDocument.SingletonId)
        {
            issues.Add("collections.settings", "settings_singleton");
        }

        if (!docs[MongoCollections.Users].Any(u => u.TryGetValue("active", out var active) && active.IsBoolean && active.AsBoolean))
        {
            issues.Add("collections.users", "no_active_user");
        }

        if (settings.Count == 0 || !settings[0].TryGetValue("timezone", out var timezone) || !timezone.IsString)
        {
            return;
        }

        string today;
        try
        {
            today = DayKeys.ToDayKey(now, DayKeys.FindZone(timezone.AsString)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        var first = settings[0];
        if (first.TryGetValue("bonusSchedule", out var schedule) && schedule.IsBsonArray)
        {
            for (var i = 0; i < schedule.AsBsonArray.Count; i++)
            {
                if (schedule.AsBsonArray[i] is { IsBsonDocument: true } row && row.AsBsonDocument.TryGetValue("from", out var from) && from.IsString
                    && string.CompareOrdinal(from.AsString, today) > 0)
                {
                    issues.Add($"collections.settings.0.bonusSchedule.{i}.from", "bonus_schedule_in_future");
                }
            }
        }

        if (first.TryGetValue("bonusFloor", out var floor) && floor.IsString && string.CompareOrdinal(floor.AsString, today) > 0)
        {
            issues.Add("collections.settings.0.bonusFloor", "bonus_floor_in_future");
        }
    }

    /// <summary>
    /// Documents that would violate a unique index: a repeated id, a repeated cycle index, a generated occurrence slot or a request key used twice. The
    /// audit log is merged by id on import and a repeat there is simply not added again.
    /// </summary>
    private static void CheckDuplicates(Dictionary<string, List<BsonDocument>> docs, ImportIssues issues)
    {
        foreach (var name in Order.Where(n => n != MongoCollections.AuditLog))
        {
            var ids = new HashSet<ObjectId>();
            for (var i = 0; i < docs[name].Count; i++)
            {
                if (!ids.Add(docs[name][i]["_id"].AsObjectId))
                {
                    issues.Add($"collections.{name}.{i}._id", "duplicate_id");
                }
            }
        }

        var indexes = new HashSet<long>();
        var cycles = docs[MongoCollections.Cycles];
        for (var i = 0; i < cycles.Count; i++)
        {
            Shape.TryInteger(cycles[i]["index"], out var index);
            if (!indexes.Add(index))
            {
                issues.Add($"collections.cycles.{i}.index", "duplicate_index");
            }
        }

        var slots = new HashSet<string>(StringComparer.Ordinal);
        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = docs[MongoCollections.Occurrences];
        for (var i = 0; i < occurrences.Count; i++)
        {
            var doc = occurrences[i];
            var path = $"collections.occurrences.{i}";
            if (doc["origin"].AsString == "generated")
            {
                var task = doc["taskId"].IsObjectId ? doc["taskId"].AsObjectId.ToString() : "null";
                var key = $"{doc["cycleId"].AsObjectId}:{task}:{doc["plannedDate"].ToUniversalTime().Ticks}";
                if (!slots.Add(key))
                {
                    issues.Add($"{path}.plannedDate", "duplicate_slot");
                }
            }

            if (doc.TryGetValue("requestId", out var request) && request.IsString && !requestIds.Add(request.AsString))
            {
                issues.Add($"{path}.requestId", "duplicate_request_id");
            }
        }
    }

    /// <summary>A redemption of a person who is not in the file, a repeated ledger key or a repeated request key cannot be put back.</summary>
    private static void CheckRedemptions(Dictionary<string, List<BsonDocument>> docs, ImportIssues issues)
    {
        var people = docs[MongoCollections.Users].Select(u => u["_id"].AsObjectId).ToHashSet();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        var redemptions = docs[MongoCollections.PointEntries];
        for (var i = 0; i < redemptions.Count; i++)
        {
            var doc = redemptions[i];
            var path = $"collections.pointEntries.{i}";
            if (!people.Contains(doc["personId"].AsObjectId))
            {
                issues.Add($"{path}.personId", "unknown_user");
            }

            if (!keys.Add(doc["key"].AsString))
            {
                issues.Add($"{path}.key", "duplicate_key");
            }

            if (doc["requestId"].IsString && !requestIds.Add(doc["requestId"].AsString))
            {
                issues.Add($"{path}.requestId", "duplicate_request_id");
            }
        }
    }

    /// <summary>
    /// Brings the badges of a file in line with its tasks (ADR-0014): a task the file does not have (it was deleted before an older export) is dropped from
    /// the rule, the tasks of a rule are put in a stable order, and a rule that named tasks and names none left is deactivated, because an empty list would
    /// count every task.
    /// </summary>
    private static void ReconcileBadgeTasks(List<BsonDocument> badges, List<BsonDocument> tasks)
    {
        var known = tasks.Select(t => t["_id"].AsObjectId).ToHashSet();
        foreach (var badge in badges)
        {
            if (badge["rule"] is not BsonDocument rule || !rule.TryGetValue("taskIds", out var ids) || !ids.IsBsonArray)
            {
                continue;
            }

            var before = ids.AsBsonArray.Count;
            var kept = ids.AsBsonArray.Select(i => i.AsObjectId).Where(known.Contains).OrderBy(i => i.ToString(), StringComparer.Ordinal).ToList();
            rule["taskIds"] = new BsonArray(kept);
            if (before > 0 && kept.Count == 0)
            {
                badge["active"] = false;
            }
        }
    }

    /// <summary>An image whose bytes are not what the file says they are (size, hash, a real PNG, JPEG or WebP of the declared type; an SVG is refused), or a repeated example key.</summary>
    private static void CheckBadges(List<BsonDocument> badges, ImportIssues issues)
    {
        var exampleKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < badges.Count; i++)
        {
            var badge = badges[i];
            var path = $"collections.badges.{i}";
            if (badge.TryGetValue("exampleKey", out var example) && example.IsString && !exampleKeys.Add(example.AsString))
            {
                issues.Add($"{path}.exampleKey", "duplicate_example_key");
            }

            if (!badge.TryGetValue("image", out var image) || image is not BsonDocument picture)
            {
                continue;
            }

            var bytes = picture["data"].AsBsonBinaryData.Bytes;
            Shape.TryInteger(picture["size"], out var size);
            if (bytes.Length != size)
            {
                issues.Add($"{path}.image.size", "image_size_mismatch");
            }

            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), picture["hash"].AsString, StringComparison.Ordinal))
            {
                issues.Add($"{path}.image.hash", "image_hash_mismatch");
            }

            if (BadgeImages.Sniff(bytes) is not { } type)
            {
                issues.Add($"{path}.image.data", "unsupported_image_type");
            }
            else if (type.ContentType() != picture["contentType"].AsString)
            {
                issues.Add($"{path}.image.contentType", "image_type_mismatch");
            }
        }
    }
}
