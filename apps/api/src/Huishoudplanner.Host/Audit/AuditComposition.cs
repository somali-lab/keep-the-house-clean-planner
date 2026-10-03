using Huishoudplanner.Adapters.Mongo.Audit;
using Huishoudplanner.Application.Audit;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Configuration;
using Microsoft.Extensions.Options;
using OneOf;

namespace Huishoudplanner.Host.Audit;

/// <summary>The wiring of the audit log slice: the use cases, the Mongo ports and the configured retention.</summary>
public static class AuditComposition
{
    public static IServiceCollection AddAuditLog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoAuditLog();
        services.AddSingleton<ForReadingAuditRetention, ConfiguredAuditRetention>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IAuditRetentionService, AuditRetentionService>();
        return services;
    }
}

/// <summary>
/// <see cref="ForReadingAuditRetention"/> from <c>AUDIT_RETENTION_DAYS</c> (<see cref="AppOptions.AuditRetentionDays"/>), which is
/// configuration in the Node server as well, not a setting.
/// </summary>
internal sealed class ConfiguredAuditRetention(IOptions<AppOptions> options) : ForReadingAuditRetention
{
    public Task<OneOf<AuditRetentionPolicy, PortError>> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<AuditRetentionPolicy, PortError>>(new AuditRetentionPolicy(options.Value.AuditRetentionDays));
}
