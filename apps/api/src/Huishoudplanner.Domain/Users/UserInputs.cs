namespace Huishoudplanner.Domain.Users;

// The raw request values, as a client sent them: every member may be absent (null) and nothing is validated yet.
// UserRules turns them into NewUser / UserPatch or into the field errors of a 400.

public sealed record DailyMinutesInput(int? Weekday, int? Weekend);

public sealed record BrowserNotificationsInput(bool? Enabled, IReadOnlyList<string>? Times);

public sealed record CreateUserInput(
    string? Name,
    string? Color,
    string? Role,
    IReadOnlyList<int>? UnavailableWeekdays,
    DailyMinutesInput? DailyBudgetMinutes,
    DailyMinutesInput? MaxDailyMinutes);

public sealed record UpdateUserInput(
    string? Name = null,
    string? Color = null,
    bool? Active = null,
    string? Role = null,
    IReadOnlyList<int>? UnavailableWeekdays = null,
    DailyMinutesInput? DailyBudgetMinutes = null,
    DailyMinutesInput? MaxDailyMinutes = null,
    BrowserNotificationsInput? BrowserNotifications = null);

/// <summary>A profile created on an empty database (<c>SEED_USERS</c>).</summary>
public sealed record SeedProfile(string Name, string Color);

/// <summary>One page of users, oldest first. <see cref="NextCursor"/> is <see langword="null"/> on the last page.</summary>
public sealed record UserPage(IReadOnlyList<User> Items, string? NextCursor);

/// <summary>What a list asks the store for. <see cref="Limit"/> is already within bounds.</summary>
public sealed record UserQuery(bool? Active, UserCursor? After, int Limit);
