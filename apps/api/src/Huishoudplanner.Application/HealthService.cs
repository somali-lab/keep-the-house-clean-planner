using Huishoudplanner.Domain.Health;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Application;

public sealed partial class HealthService(ForCheckingHealth database, ILogger<HealthService> logger) : IHealthService
{
    public async Task<HealthReport> GetReportAsync(CancellationToken cancellationToken)
    {
        var result = await database.CheckDatabaseAsync(cancellationToken).ConfigureAwait(false);
        // The port error message is written for logs and never contains a connection string.
        result.Switch(_ => { }, error => LogDatabaseUnavailable(logger, error.Message));
        return new HealthReport(result.IsT0);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health check failed: {Reason}")]
    private static partial void LogDatabaseUnavailable(ILogger logger, string reason);
}
