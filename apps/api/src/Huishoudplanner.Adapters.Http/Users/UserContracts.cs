using System.ComponentModel;
using Huishoudplanner.Domain.Users;

namespace Huishoudplanner.Adapters.Http.Users;

// The request records below only document the bodies in OpenAPI; the bodies are read by UserRequestParser (Zod-style
// validation_error for a wrong type, null, malformed JSON or an empty body), never bound to these types.

/// <summary>A budget or ceiling in minutes: Monday to Friday together and Saturday and Sunday together. Both values are required.</summary>
public sealed record DailyMinutesBody(int? Weekday, int? Weekend);

/// <summary>Browser notification moments (ADR-0010). Both members are required; <c>times</c> are unique <c>HH:mm</c> values, at most six, read in the household timezone.</summary>
public sealed record BrowserNotificationsBody(bool? Enabled, string[]? Times);

/// <summary>Creates a person. <c>name</c> and <c>color</c> (<c>#rrggbb</c>) are required; the rest defaults to a member available every day with the budget and ceiling 60/120.</summary>
public sealed record CreateUserRequest(
    string? Name,
    string? Color,
    [property: Description("admin, planner or member. Default member.")] string? Role,
    [property: Description("Days of the week the person is unavailable, 0 (Sunday) to 6 (Saturday); stored unique and sorted.")] int[]? UnavailableWeekdays,
    DailyMinutesBody? DailyBudgetMinutes,
    DailyMinutesBody? MaxDailyMinutes);

/// <summary>Changes a person. Every member is optional; a member that is left out stays as it is. A person is deactivated with <c>active: false</c>, never deleted.</summary>
public sealed record UpdateUserRequest(
    string? Name,
    string? Color,
    bool? Active,
    [property: Description("admin, planner or member.")] string? Role,
    int[]? UnavailableWeekdays,
    DailyMinutesBody? DailyBudgetMinutes,
    DailyMinutesBody? MaxDailyMinutes,
    BrowserNotificationsBody? BrowserNotifications);

public sealed record DailyMinutesResponse(int Weekday, int Weekend);

public sealed record BrowserNotificationsResponse(bool Enabled, string[] Times);

public sealed record UserResponse(
    string Id,
    string Name,
    string Color,
    bool Active,
    [property: Description("admin, planner or member.")] string Role,
    int[] UnavailableWeekdays,
    [property: Description("Target per cycle week: Monday to Friday together and Saturday and Sunday together.")] DailyMinutesResponse DailyBudgetMinutes,
    [property: Description("Ceiling for one single day.")] DailyMinutesResponse MaxDailyMinutes,
    BrowserNotificationsResponse BrowserNotifications,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: Description("The concurrency version of the person (the number inside its ETag); send the ETag as If-Match when you change the person.")] int Version)
{
    public static UserResponse From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserResponse(
            user.Id,
            user.Name,
            user.Color,
            user.Active,
            UserRules.RoleName(user.Role),
            [.. user.UnavailableWeekdays],
            new DailyMinutesResponse(user.DailyBudgetMinutes.Weekday, user.DailyBudgetMinutes.Weekend),
            new DailyMinutesResponse(user.MaxDailyMinutes.Weekday, user.MaxDailyMinutes.Weekend),
            new BrowserNotificationsResponse(user.BrowserNotifications.Enabled, [.. user.BrowserNotifications.Times]),
            user.CreatedAt,
            user.UpdatedAt,
            user.Version);
    }
}

/// <summary>One page of people, oldest first. <c>nextCursor</c> is null on the last page and otherwise the value to pass as <c>cursor</c>.</summary>
public sealed record UserListResponse(IReadOnlyList<UserResponse> Items, string? NextCursor);
