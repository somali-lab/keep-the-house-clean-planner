namespace Huishoudplanner.Adapters.Mongo;

/// <summary>Collection names, as in apps/server/src/data/db.ts.</summary>
public static class MongoCollections
{
    public const string Users = "users";
    public const string Rooms = "rooms";
    public const string Tasks = "tasks";
    public const string CyclePlans = "cyclePlans";
    public const string Cycles = "cycles";
    public const string Occurrences = "occurrences";
    public const string AuditLog = "auditLog";
    public const string Settings = "settings";
    public const string PointEntries = "pointEntries";
    public const string Badges = "badges";
    public const string BadgeAwards = "badgeAwards";
}
