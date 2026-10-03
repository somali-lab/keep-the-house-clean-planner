using Huishoudplanner.Domain.Health;

namespace Huishoudplanner.Domain.Ports.Driving;

public interface IHealthService
{
    Task<HealthReport> GetReportAsync(CancellationToken cancellationToken);
}
