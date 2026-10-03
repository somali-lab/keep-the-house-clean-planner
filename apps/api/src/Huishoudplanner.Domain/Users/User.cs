using Huishoudplanner.Domain.Identity;

namespace Huishoudplanner.Domain.Users;

/// <summary>A budget or ceiling in minutes: Monday to Friday together (weekday) and Saturday and Sunday together (weekend).</summary>
public sealed record DailyMinutes(int Weekday, int Weekend);

/// <summary>
/// The per-person moments for browser notifications (ADR-0010): 'HH:mm' on a 24-hour clock in the household timezone,
/// unique and sorted, at most <see cref="UserLimits.MaxBrowserNotificationTimes"/>.
/// </summary>
public sealed record BrowserNotifications(bool Enabled, IReadOnlyList<string> Times)
{
    /// <summary>What a user without stored notification settings reads as.</summary>
    public static BrowserNotifications Disabled { get; } = new(false, []);

    public bool Equals(BrowserNotifications? other) =>
        other is not null && Enabled == other.Enabled && Times.SequenceEqual(other.Times, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Enabled, Times.Count);
}

/// <summary>A person of the household (requirements section 3, <c>users</c>). <see cref="Id"/> is the 24-character hex id.</summary>
public sealed record User(
    string Id,
    string Name,
    string Color,
    bool Active,
    Role Role,
    IReadOnlyList<int> UnavailableWeekdays,
    DailyMinutes DailyBudgetMinutes,
    DailyMinutes MaxDailyMinutes,
    BrowserNotifications BrowserNotifications,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A validated, normalised person that does not exist yet. The store assigns the id.</summary>
public sealed record NewUser(
    string Name,
    string Color,
    Role Role,
    IReadOnlyList<int> UnavailableWeekdays,
    DailyMinutes DailyBudgetMinutes,
    DailyMinutes MaxDailyMinutes);

/// <summary>
/// A validated, normalised change of a person: only the members that are not <see langword="null"/> are set.
/// Weekdays and notification times are already unique and sorted, the name is trimmed.
/// </summary>
public sealed record UserPatch(
    string? Name = null,
    string? Color = null,
    bool? Active = null,
    Role? Role = null,
    IReadOnlyList<int>? UnavailableWeekdays = null,
    DailyMinutes? DailyBudgetMinutes = null,
    DailyMinutes? MaxDailyMinutes = null,
    BrowserNotifications? BrowserNotifications = null);

/// <summary>The values the household starts with and the values legacy documents read as.</summary>
public static class UserDefaults
{
    /// <summary>A new user's budget and daily ceiling unless the request gives them.</summary>
    public static DailyMinutes NewUserMinutes { get; } = new(60, 120);

    /// <summary>A user stored without <c>maxDailyMinutes</c> reads as this.</summary>
    public static DailyMinutes LegacyMaxDailyMinutes { get; } = new(480, 480);

    /// <summary>A user stored without a role reads as an administrator: installations from before roles keep their access.</summary>
    public const Role LegacyRole = Role.Admin;
}

public static class UserLimits
{
    public const int MaxBrowserNotificationTimes = 6;

    public const int DefaultPageSize = 100;

    public const int MaxPageSize = 500;
}
