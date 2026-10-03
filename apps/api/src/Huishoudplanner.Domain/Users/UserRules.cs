using System.Globalization;
using System.Text.RegularExpressions;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using OneOf;

namespace Huishoudplanner.Domain.Users;

/// <summary>
/// The rules of the users resource: what a valid person is (the limits of <c>packages/shared/src/schemas/users.ts</c>),
/// how input is normalised, how a patch applies, the last-administrator rule and the audit shape. Field errors use the
/// dotted paths of the Node server (<c>unavailableWeekdays.0</c>, <c>browserNotifications.times.1</c>).
/// </summary>
public static partial class UserRules
{
    public const string InvalidColor = "invalid_color";
    public const string InvalidTime = "invalid_time";
    public const string DuplicateTime = "duplicate_time";
    public const string InvalidObjectId = "invalid_object_id";
    public const string LastAdminCode = "last_admin";
    public const string LastAdminDetail = "At least one active administrator is required";

    [GeneratedRegex(@"\A#[0-9a-fA-F]{6}\z")]
    private static partial Regex ColorPattern();

    [GeneratedRegex(@"\A([01][0-9]|2[0-3]):[0-5][0-9]\z")]
    private static partial Regex TimePattern();

    /// <summary>A 24-character hexadecimal id, either case.</summary>
    public static bool IsObjectId(string? value) => AuditObjectId.IsHex(value);

    public static OneOf<NewUser, ValidationErrors> ParseCreate(CreateUserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new FieldErrors();
        var name = ParseName(input.Name, required: true, errors);
        var color = ParseColor(input.Color, required: true, errors);
        var role = input.Role is null ? Role.Member : ParseRole(input.Role, errors);
        var weekdays = ParseWeekdays(input.UnavailableWeekdays, errors) ?? [];
        var budget = ParseMinutes(input.DailyBudgetMinutes, "dailyBudgetMinutes", errors) ?? UserDefaults.NewUserMinutes;
        var max = ParseMinutes(input.MaxDailyMinutes, "maxDailyMinutes", errors) ?? UserDefaults.NewUserMinutes;
        if (errors.Any || name is null || color is null || role is null)
        {
            return errors.ToValidationErrors();
        }

        return new NewUser(name, color, role.Value, weekdays, budget, max);
    }

    public static OneOf<UserPatch, ValidationErrors> ParsePatch(UpdateUserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new FieldErrors();
        var name = ParseName(input.Name, required: false, errors);
        var color = ParseColor(input.Color, required: false, errors);
        var role = input.Role is null ? null : ParseRole(input.Role, errors);
        var weekdays = ParseWeekdays(input.UnavailableWeekdays, errors);
        var budget = ParseMinutes(input.DailyBudgetMinutes, "dailyBudgetMinutes", errors);
        var max = ParseMinutes(input.MaxDailyMinutes, "maxDailyMinutes", errors);
        var notifications = input.BrowserNotifications is null ? null : ParseNotifications(input.BrowserNotifications, "browserNotifications.", errors);
        return errors.Any
            ? errors.ToValidationErrors()
            : new UserPatch(name, color, input.Active, role, weekdays, budget, max, notifications);
    }

    /// <summary>The complete setting that replaces the stored one. Times may arrive in any order; they are stored sorted.</summary>
    public static OneOf<BrowserNotifications, ValidationErrors> ParseBrowserNotifications(BrowserNotificationsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new FieldErrors();
        var parsed = ParseNotifications(input, string.Empty, errors);
        return errors.Any || parsed is null ? errors.ToValidationErrors() : parsed;
    }

    /// <summary>The user would stop being an active administrator: deactivated, or given another role.</summary>
    public static bool RemovesActiveAdmin(User before, UserPatch patch)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(patch);
        return before.Active && before.Role == Role.Admin &&
               (patch.Active == false || (patch.Role is { } role && role != Role.Admin));
    }

    public static User Apply(User before, UserPatch patch, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(patch);
        return before with
        {
            Name = patch.Name ?? before.Name,
            Color = patch.Color ?? before.Color,
            Active = patch.Active ?? before.Active,
            Role = patch.Role ?? before.Role,
            UnavailableWeekdays = patch.UnavailableWeekdays ?? before.UnavailableWeekdays,
            DailyBudgetMinutes = patch.DailyBudgetMinutes ?? before.DailyBudgetMinutes,
            MaxDailyMinutes = patch.MaxDailyMinutes ?? before.MaxDailyMinutes,
            BrowserNotifications = patch.BrowserNotifications ?? before.BrowserNotifications,
            UpdatedAt = now,
        };
    }

    /// <summary>The audited fields of a user (everything but the id and the timestamps), as the Node server diffs them.</summary>
    public static AuditObject ToAudit(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return AuditObject.Of(
            ("name", user.Name),
            ("color", user.Color),
            ("active", user.Active),
            ("role", RoleName(user.Role)),
            ("unavailableWeekdays", new AuditArray([.. user.UnavailableWeekdays.Select(d => (AuditValue)d)])),
            ("dailyBudgetMinutes", Minutes(user.DailyBudgetMinutes)),
            ("maxDailyMinutes", Minutes(user.MaxDailyMinutes)),
            ("browserNotifications", AuditObject.Of(
                ("enabled", user.BrowserNotifications.Enabled),
                ("times", new AuditArray([.. user.BrowserNotifications.Times.Select(t => (AuditValue)t)])))));
    }

    /// <summary>The wire name of a role: <c>admin</c>, <c>planner</c> or <c>member</c>.</summary>
    public static string RoleName(Role role) => role switch
    {
        Role.Admin => "admin",
        Role.Planner => "planner",
        Role.Member => "member",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static AuditObject Minutes(DailyMinutes minutes) =>
        AuditObject.Of(("weekday", minutes.Weekday), ("weekend", minutes.Weekend));

    private static string? ParseName(string? value, bool required, FieldErrors errors)
    {
        if (value is null)
        {
            if (required)
            {
                errors.Add("name", "required");
            }

            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            errors.Add("name", "must not be empty");
            return null;
        }

        return trimmed;
    }

    private static string? ParseColor(string? value, bool required, FieldErrors errors)
    {
        if (value is null)
        {
            if (required)
            {
                errors.Add("color", "required");
            }

            return null;
        }

        if (!ColorPattern().IsMatch(value))
        {
            errors.Add("color", InvalidColor);
            return null;
        }

        return value;
    }

    private static Role? ParseRole(string value, FieldErrors errors)
    {
        switch (value)
        {
            case "admin":
                return Role.Admin;
            case "planner":
                return Role.Planner;
            case "member":
                return Role.Member;
            default:
                errors.Add("role", "must be admin, planner or member");
                return null;
        }
    }

    private static List<int>? ParseWeekdays(IReadOnlyList<int>? days, FieldErrors errors)
    {
        if (days is null)
        {
            return null;
        }

        var valid = true;
        for (var i = 0; i < days.Count; i++)
        {
            if (days[i] is < 0 or > 6)
            {
                errors.Add($"unavailableWeekdays.{i.ToString(CultureInfo.InvariantCulture)}", "must be between 0 (Sunday) and 6 (Saturday)");
                valid = false;
            }
        }

        return valid ? [.. days.Distinct().Order()] : null;
    }

    private static DailyMinutes? ParseMinutes(DailyMinutesInput? input, string field, FieldErrors errors)
    {
        if (input is null)
        {
            return null;
        }

        var weekday = ParseMinute(input.Weekday, $"{field}.weekday", errors);
        var weekend = ParseMinute(input.Weekend, $"{field}.weekend", errors);
        return weekday is { } wd && weekend is { } we ? new DailyMinutes(wd, we) : null;
    }

    private static int? ParseMinute(int? value, string field, FieldErrors errors)
    {
        if (value is null)
        {
            errors.Add(field, "required");
            return null;
        }

        if (value < 0)
        {
            errors.Add(field, "must not be negative");
            return null;
        }

        return value;
    }

    private static BrowserNotifications? ParseNotifications(BrowserNotificationsInput input, string prefix, FieldErrors errors)
    {
        var enabled = input.Enabled;
        if (enabled is null)
        {
            errors.Add($"{prefix}enabled", "required");
        }

        var times = input.Times;
        if (times is null)
        {
            errors.Add($"{prefix}times", "required");
            return null;
        }

        var valid = true;
        for (var i = 0; i < times.Count; i++)
        {
            if (!TimePattern().IsMatch(times[i] ?? string.Empty))
            {
                errors.Add($"{prefix}times.{i.ToString(CultureInfo.InvariantCulture)}", InvalidTime);
                valid = false;
            }
        }

        if (times.Count > UserLimits.MaxBrowserNotificationTimes)
        {
            errors.Add($"{prefix}times", $"at most {UserLimits.MaxBrowserNotificationTimes} moments");
            valid = false;
        }

        if (valid && times.Distinct(StringComparer.Ordinal).Count() != times.Count)
        {
            errors.Add($"{prefix}times", DuplicateTime);
            valid = false;
        }

        return valid && enabled is { } isEnabled ? new BrowserNotifications(isEnabled, [.. times.Order(StringComparer.Ordinal)]) : null;
    }

    private sealed class FieldErrors
    {
        private readonly Dictionary<string, List<string>> errors = new(StringComparer.Ordinal);

        public bool Any => errors.Count > 0;

        public void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var messages))
            {
                errors[field] = messages = [];
            }

            messages.Add(message);
        }

        public ValidationErrors ToValidationErrors() =>
            new(errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
    }
}
