using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>Reads the configured cycle anchor date (a Monday) from the settings.</summary>
public interface ForReadingCycleAnchor
{
    Task<OneOf<DateOnly, SettingsMissing, PortError>> GetAnchorAsync(CancellationToken cancellationToken);
}
