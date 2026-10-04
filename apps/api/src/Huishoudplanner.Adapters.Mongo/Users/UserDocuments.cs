using Huishoudplanner.Domain.Concurrency;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Users;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Users;

/// <summary>
/// The <c>users</c> document, mapped by hand so documents written by the Node server (or an older version) stay readable:
/// <c>_id, name, color, active, role, unavailableWeekdays, dailyBudgetMinutes, maxDailyMinutes, browserNotifications,
/// createdAt, updatedAt</c>, integers as int32, instants as BSON dates. Reading is lenient and applies the defaults of
/// requirements section 3: no role is an administrator, an unknown role the least privilege, no daily maximum 480/480,
/// no notification settings disabled, a non-boolean <c>active</c> inactive.
/// </summary>
internal static class UserDocuments
{
    public static BsonDocument ToDocument(ObjectId id, NewUser user, DateTimeOffset now) => new()
    {
        { "_id", id },
        { "name", user.Name },
        { "color", user.Color },
        { "active", true },
        { "role", UserRules.RoleName(user.Role) },
        { "unavailableWeekdays", new BsonArray(user.UnavailableWeekdays) },
        { "dailyBudgetMinutes", Minutes(user.DailyBudgetMinutes) },
        { "maxDailyMinutes", Minutes(user.MaxDailyMinutes) },
        { "browserNotifications", Notifications(BrowserNotifications.Disabled) },
        { "createdAt", new BsonDateTime(now.UtcDateTime) },
        { "updatedAt", new BsonDateTime(now.UtcDateTime) },
        { EntityVersioning.Field, EntityVersion.Initial },
    };

    /// <summary>One <c>$set</c> per member of the patch that is set, plus <c>updatedAt</c>.</summary>
    public static List<UpdateDefinition<BsonDocument>> ToSets(UserPatch patch, DateTimeOffset now)
    {
        var update = Builders<BsonDocument>.Update;
        var sets = new List<UpdateDefinition<BsonDocument>>();
        if (patch.Name is { } name)
        {
            sets.Add(update.Set("name", name));
        }

        if (patch.Color is { } color)
        {
            sets.Add(update.Set("color", color));
        }

        if (patch.Active is { } active)
        {
            sets.Add(update.Set("active", active));
        }

        if (patch.Role is { } role)
        {
            sets.Add(update.Set("role", UserRules.RoleName(role)));
        }

        if (patch.UnavailableWeekdays is { } weekdays)
        {
            sets.Add(update.Set("unavailableWeekdays", new BsonArray(weekdays)));
        }

        if (patch.DailyBudgetMinutes is { } budget)
        {
            sets.Add(update.Set("dailyBudgetMinutes", Minutes(budget)));
        }

        if (patch.MaxDailyMinutes is { } max)
        {
            sets.Add(update.Set("maxDailyMinutes", Minutes(max)));
        }

        if (patch.BrowserNotifications is { } notifications)
        {
            sets.Add(update.Set("browserNotifications", Notifications(notifications)));
        }

        sets.Add(update.Set("updatedAt", new BsonDateTime(now.UtcDateTime)));
        return sets;
    }

    public static User ToUser(BsonDocument document) => new(
        document["_id"].AsObjectId.ToString(),
        document.TryGetValue("name", out var name) && name.IsString ? name.AsString : string.Empty,
        document.TryGetValue("color", out var color) && color.IsString ? color.AsString : string.Empty,
        IsActive(document),
        RoleOf(document),
        Weekdays(document),
        MinutesOf(document, "dailyBudgetMinutes") ?? new DailyMinutes(0, 0),
        MinutesOf(document, "maxDailyMinutes") ?? UserDefaults.LegacyMaxDailyMinutes,
        NotificationsOf(document),
        InstantOf(document, "createdAt"),
        InstantOf(document, "updatedAt"),
        EntityVersioning.VersionOf(document));

    public static bool IsActive(BsonDocument document) =>
        document.TryGetValue("active", out var active) && active.IsBoolean && active.AsBoolean;

    public static Role RoleOf(BsonDocument document)
    {
        if (!document.TryGetValue("role", out var role) || role.IsBsonNull)
        {
            return UserDefaults.LegacyRole;
        }

        return role.IsString ? role.AsString switch
        {
            "admin" => Role.Admin,
            "planner" => Role.Planner,
            _ => Role.Member,
        }
        : Role.Member;
    }

    private static BsonDocument Minutes(DailyMinutes minutes) => new() { { "weekday", minutes.Weekday }, { "weekend", minutes.Weekend } };

    private static BsonDocument Notifications(BrowserNotifications notifications) =>
        new() { { "enabled", notifications.Enabled }, { "times", new BsonArray(notifications.Times) } };

    private static List<int> Weekdays(BsonDocument document) =>
        document.TryGetValue("unavailableWeekdays", out var days) && days.IsBsonArray
            ? [.. days.AsBsonArray.Where(d => d.IsNumeric).Select(d => d.ToInt32())]
            : [];

    private static DailyMinutes? MinutesOf(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsBsonDocument
            ? new DailyMinutes(Int(value.AsBsonDocument, "weekday"), Int(value.AsBsonDocument, "weekend"))
            : null;

    private static int Int(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsNumeric ? value.ToInt32() : 0;

    private static BrowserNotifications NotificationsOf(BsonDocument document)
    {
        if (!document.TryGetValue("browserNotifications", out var value) || !value.IsBsonDocument)
        {
            return BrowserNotifications.Disabled;
        }

        var stored = value.AsBsonDocument;
        var enabled = stored.TryGetValue("enabled", out var flag) && flag.IsBoolean && flag.AsBoolean;
        var times = stored.TryGetValue("times", out var list) && list.IsBsonArray
            ? list.AsBsonArray.Where(t => t.IsString).Select(t => t.AsString).ToList()
            : [];
        return new BrowserNotifications(enabled, times);
    }

    private static DateTimeOffset InstantOf(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsValidDateTime
            ? new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
            : DateTimeOffset.UnixEpoch;
}
