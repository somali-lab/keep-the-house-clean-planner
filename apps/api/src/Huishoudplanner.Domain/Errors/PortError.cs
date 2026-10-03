namespace Huishoudplanner.Domain.Errors;

/// <summary>
/// An infrastructure failure behind a driven port (database unreachable, timeout). <paramref name="Code"/> is a stable
/// machine-readable code; <paramref name="Message"/> is value-free: never a connection string or other configuration.
/// </summary>
public sealed record PortError(string Code, string Message);
