using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The household settings (requirements 3 and 4.12). Who may call what (everyone reads, administrators write) is decided by
/// the driving adapter; the <see cref="Actor"/> is attribution for the audit entry.
/// </summary>
public interface ISettingsService
{
    Task<OneOf<SettingsView, SettingsMissing, PortError>> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Changes the given fields in one transaction with one audit entry; a patch that changes nothing writes and audits nothing
    /// and returns the settings as they are. Removing an interval that tasks use is <see cref="IntervalInUse"/>. Setting the
    /// bonus amounts writes a schedule row from today; when concurrent writers keep winning the answer is
    /// <c>409 bonus_schedule_conflict</c>.
    /// </summary>
    Task<OneOf<SettingsView, ValidationErrors, IntervalInUse, ConflictError, SettingsMissing, PortError>> UpdateAsync(
        Actor actor, SettingsPatch patch, CancellationToken cancellationToken);
}
