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

    /// <summary>One guard document per person (<c>_id</c> is the person): the redemptions of a person write it first, so that two bookings in flight conflict (ADR-0021). Not known to the Node server, never exported.</summary>
    public const string PointGuards = "pointGuards";

    public const string Badges = "badges";
    public const string BadgeAwards = "badgeAwards";
    public const string Migrations = "migrations";
}
