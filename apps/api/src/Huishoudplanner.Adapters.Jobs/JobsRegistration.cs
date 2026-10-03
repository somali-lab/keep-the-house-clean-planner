using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Huishoudplanner.Adapters.Jobs;

public static class JobsRegistration
{
    /// <summary>
    /// Registers the scheduler (one hosted service over the registry of jobs), the runner and the jobs that exist today. The options come
    /// from the composition root, so this project knows no configuration type. Not called during build-time OpenAPI generation.
    /// </summary>
    public static IServiceCollection AddJobsAdapter(this IServiceCollection services, Func<IServiceProvider, JobsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<JobRunner>();
        services.TryAddSingleton<JobScheduler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobSchedulerService>());
        services.AddJob<NightlyGenerationJob>();
        services.AddJob<AuditRetentionJob>();
        return services;
    }

    /// <summary>Adds a job to the registry. The scheduler picks up every registered <see cref="IJob"/>.</summary>
    public static IServiceCollection AddJob<TJob>(this IServiceCollection services)
        where TJob : class, IJob
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJob, TJob>());
        return services;
    }
}
