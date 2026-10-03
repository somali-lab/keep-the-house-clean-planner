using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Audit;

public static class MongoAuditLogRegistration
{
    /// <summary>
    /// Registers the driven ports of the audit log slice (read, clear and retention delete, occurrence context). Needs
    /// <see cref="MongoAdapterRegistration.AddMongoAdapter"/>. <c>ForReadingAuditRetention</c> is a configuration value and
    /// comes from the composition root.
    /// </summary>
    public static IServiceCollection AddMongoAuditLog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ForReadingAuditLog>(sp =>
            new MongoAuditLogReader(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        services.AddSingleton<ForDeletingAuditEntries>(sp =>
            new MongoAuditEraser(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        services.AddSingleton<ForReadingOccurrenceContext>(sp =>
            new MongoOccurrenceContext(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
