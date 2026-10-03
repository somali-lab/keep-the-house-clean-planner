using Huishoudplanner.Domain.Health;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Application;

public sealed class HealthService(ForCheckingHealth database) : IHealthService
{
    public async Task<HealthReport> GetReportAsync(CancellationToken cancellationToken)
    {
        var result = await database.CheckDatabaseAsync(cancellationToken).ConfigureAwait(false);
        return new HealthReport(result.IsT0);
    }
}
