using MongoDB.Bson;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>Every index the app relies on (requirements 2, 3.8; reference: apps/server/src/data/db.ts INDEXES).</summary>
internal static class IndexCatalog
{
    public const string GeneratedSlotIndex = "occurrences_generated_slot_unique";

    /// <summary>Creation order follows the Node list.</summary>
    public static IReadOnlyList<(string Collection, IReadOnlyList<IndexSpec> Indexes)> All { get; } =
    [
        (MongoCollections.Users, []),
        (MongoCollections.Rooms, []),
        (MongoCollections.Settings, []),
        (MongoCollections.Tasks, [Index(("roomId", 1), ("active", 1))]),
        (MongoCollections.CyclePlans, [Index(("slots.weekIndex", 1), ("slots.weekday", 1))]),
        (MongoCollections.Cycles, [Index(("index", 1)) with { Unique = true }]),
        (MongoCollections.Occurrences,
        [
            Index(("date", 1), ("assigneeId", 1)),
            Index(("status", 1), ("date", 1)),
            Index(("taskId", 1), ("completedAt", -1)),
            // ADR-0014: the executions credited to a person are found through the person and the status.
            Index(("completedBy", 1), ("status", 1)),
            Index(("assigneeId", 1), ("status", 1)),
            // Requirements 4.12: the work planned for a period is found by the day it was planned for.
            Index(("plannedDate", 1)),
            new IndexSpec(
                [("cycleId", 1), ("taskId", 1), ("plannedDate", 1)],
                GeneratedSlotIndex,
                Unique: true,
                PartialFilter: new BsonDocument("origin", "generated")),
            new IndexSpec(
                [("requestId", 1)],
                "occurrences_request_id_unique",
                Unique: true,
                PartialFilter: StringTyped("requestId")),
        ]),
        (MongoCollections.AuditLog, [Index(("entity", 1), ("entityId", 1), ("at", -1)), Index(("at", -1))]),
        // ADR-0011: the key makes the ledger idempotent, one entry per execution.
        (MongoCollections.PointEntries,
        [
            new IndexSpec([("key", 1)], "pointEntries_key_unique", Unique: true),
            Index(("personId", 1), ("date", -1)),
            Index(("date", 1)),
            // Requirements 4.12: a redemption booked twice with the same request key is one booking.
            new IndexSpec(
                [("requestId", 1)],
                "pointEntries_request_id_unique",
                Unique: true,
                PartialFilter: StringTyped("requestId")),
        ]),
        // ADR-0014: a badge definition; an example is created once, by its stable key.
        (MongoCollections.Badges,
        [
            new IndexSpec(
                [("exampleKey", 1)],
                "badges_example_key_unique",
                Unique: true,
                PartialFilter: StringTyped("exampleKey")),
        ]),
        // ADR-0014: derived awards, one per badge and person.
        (MongoCollections.BadgeAwards,
        [
            new IndexSpec([("key", 1)], "badgeAwards_key_unique", Unique: true),
            Index(("personId", 1)),
            Index(("badgeId", 1)),
        ]),
    ];

    private static IndexSpec Index(params (string Field, int Direction)[] keys) => new(keys);

    private static BsonDocument StringTyped(string field) => new(field, new BsonDocument("$type", "string"));
}
