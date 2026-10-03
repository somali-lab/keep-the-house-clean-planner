using Huishoudplanner.Adapters.Http.Jobs;
using Huishoudplanner.Adapters.Jobs;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Huishoudplanner.Host.Jobs;

/// <summary>The wiring of the scheduled jobs and their manual endpoints (requirements 4.10, 9).</summary>
public static class JobsComposition
{
    /// <summary>
    /// The scheduler runs in the household timezone (<c>TZ_APP</c>); <c>DISABLE_SCHEDULER=true</c> leaves it with no job to schedule. Build-time
    /// OpenAPI generation has no configuration and no database, so no scheduler is registered then.
    /// </summary>
    public static IServiceCollection AddJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!BuildTimeGeneration.IsRunning)
        {
            services.AddJobsAdapter(sp =>
            {
                var options = sp.GetRequiredService<IOptions<AppOptions>>().Value;
                return new JobsOptions(!options.DisableScheduler, DayKeys.FindZone(options.Timezone));
            });
        }

        return services;
    }

    public static IEndpointRouteBuilder MapJobs(this IEndpointRouteBuilder routes) => routes.MapJobEndpoints();
}
