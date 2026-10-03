using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

public static class MongoAuditRegistration
{
    /// <summary>
    /// Registers <see cref="ForRecordingAudit"/> on <c>auditLog</c>. Needs the client and options of
    /// <see cref="MongoAdapterRegistration.AddMongoAdapter"/>, which calls this; the <see cref="TimeProvider"/> comes from
    /// the host (the architecture rules allow <c>TimeProvider.System</c> in Host only).
    /// </summary>
    public static IServiceCollection AddMongoAuditRecorder(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ForRecordingAudit>(sp => new MongoAuditRecorder(
            sp.GetRequiredService<IMongoClient>(),
            sp.GetRequiredService<MongoOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
