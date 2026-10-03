namespace Huishoudplanner.Domain.Settings;

/// <summary>A schedule row as the API shows it: <see cref="StartsInFuture"/> when it does not apply yet.</summary>
public sealed record BonusScheduleRowView(DateOnly From, BonusAmounts Amounts, bool StartsInFuture);

/// <summary>
/// The settings as <c>GET /settings</c> returns them: missing optional values replaced by their defaults (no bonuses,
/// EUR, 0 cents per point, automatic goals), the schedule rows flagged when they start after today, and the amounts in
/// force today (so the web app computes nothing, plan section 4.3).
/// </summary>
public sealed record SettingsView(
    string Id,
    HouseholdSettings Settings,
    string CurrencyCode,
    int CentsPerPoint,
    RewardGoals RewardGoals,
    IReadOnlyList<BonusScheduleRowView> BonusSchedule,
    BonusAmounts BonusesInForce)
{
    public static SettingsView Of(HouseholdSettings settings, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var schedule = settings.BonusSchedule ?? [];
        return new SettingsView(
            SettingsIds.Singleton,
            settings,
            settings.CurrencyCode ?? SettingsDefaults.CurrencyCode,
            settings.CentsPerPoint ?? SettingsDefaults.CentsPerPoint,
            settings.RewardGoals ?? RewardGoals.Automatic,
            [.. schedule.Select(row => new BonusScheduleRowView(row.From, row.Amounts, row.From > today))],
            Domain.Settings.BonusSchedule.AmountsOn(schedule, today));
    }
}
