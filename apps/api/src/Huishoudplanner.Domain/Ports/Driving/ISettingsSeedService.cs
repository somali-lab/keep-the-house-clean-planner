using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>First-run settings (apps/server/src/domain/seed.ts): the document of a fresh installation and the shipped interval of older ones.</summary>
public interface ISettingsSeedService
{
    /// <summary>
    /// When no settings exist, writes the defaults of <see cref="SettingsDefaults.ForNewInstallation"/> (the timezone from configuration,
    /// the cycle anchored on the Monday of this week) with a <c>create</c> audit entry of the system actor. Otherwise adds the shipped
    /// <c>3w</c> interval when it is missing (an <c>update</c> entry) and does nothing else. All in one transaction.
    /// </summary>
    Task<OneOf<SettingsSeedResult, ConflictError, PortError>> SeedAsync(CancellationToken cancellationToken);
}

public enum SettingsSeedResult
{
    Unchanged,
    Created,
    IntervalAdded,
}
