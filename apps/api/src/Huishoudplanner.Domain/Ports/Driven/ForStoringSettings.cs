using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// The singleton settings document (collection <c>settings</c>, id <see cref="SettingsIds.Singleton"/>). The write methods take
/// part in the running transaction (<see cref="ForRunningTransactions"/>) and are always paired with their audit entry by the caller.
/// </summary>
public interface ForStoringSettings
{
    Task<OneOf<HouseholdSettings, SettingsMissing, PortError>> GetAsync(CancellationToken cancellationToken);

    /// <summary>Sets the fields <paramref name="changes"/> names and the modification time; every other stored field stays as it is.</summary>
    Task<OneOf<HouseholdSettings, SettingsMissing, PortError>> UpdateAsync(SettingsChanges changes, CancellationToken cancellationToken);

    /// <summary>Inserts the document when none exists. <see langword="true"/> when it was inserted, <see langword="false"/> when one was already there.</summary>
    Task<OneOf<bool, PortError>> InsertIfMissingAsync(HouseholdSettings settings, CancellationToken cancellationToken);
}
