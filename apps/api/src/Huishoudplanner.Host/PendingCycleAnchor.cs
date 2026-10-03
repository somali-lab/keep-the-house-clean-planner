using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Host;

/// <summary>
/// Stand-in for the settings adapter until slice 1.3 (settings) lands: the installation has no readable settings yet, so the
/// calendar answers <c>500 settings_missing</c>. Slice 1.3 replaces this registration with the Mongo adapter.
/// </summary>
internal sealed class PendingCycleAnchor : ForReadingCycleAnchor
{
    public Task<OneOf<DateOnly, SettingsMissing, PortError>> GetAnchorAsync(CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<DateOnly, SettingsMissing, PortError>>(new SettingsMissing());
}
