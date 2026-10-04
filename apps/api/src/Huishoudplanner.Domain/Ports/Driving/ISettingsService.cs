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
    /// <c>409 bonus_schedule_conflict</c>. <paramref name="expectedVersion"/> is the version the caller read (<c>If-Match</c>, ADR-0022): another version is a
    /// <see cref="PreconditionFailed"/> (<c>412</c>), also for a patch that would change nothing, and it is answered before any other check, so a client that
    /// sends the ETag it read never meets <c>bonus_schedule_conflict</c>: the 409 stays as the second defence for a caller without a precondition and for a write that
    /// keeps losing to concurrent ones after the runner's last attempt. <see langword="null"/> skips the precondition.
    /// </summary>
    Task<OneOf<SettingsView, ValidationErrors, IntervalInUse, ConflictError, SettingsMissing, PortError, PreconditionFailed>> UpdateAsync(
        Actor actor, SettingsPatch patch, CancellationToken cancellationToken, int? expectedVersion = null);
}
