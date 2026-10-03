namespace Huishoudplanner.Domain.Health;

/// <summary>What the health endpoint reports: the state of each dependency.</summary>
public sealed record HealthReport(bool DatabaseReachable)
{
    public bool IsHealthy => DatabaseReachable;
}
