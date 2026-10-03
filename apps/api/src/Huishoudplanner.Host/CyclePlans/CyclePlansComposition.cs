using Huishoudplanner.Adapters.Http.CyclePlans;
using Huishoudplanner.Adapters.Mongo.CyclePlans;
using Huishoudplanner.Application.CyclePlans;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Startup;

namespace Huishoudplanner.Host.CyclePlans;

/// <summary>The wiring of the cycle plans slice: the use cases, the plan store, the first-start plan and the endpoints.</summary>
public static class CyclePlansComposition
{
    public static IServiceCollection AddCyclePlans(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoCyclePlans();
        services.AddScoped<ICyclePlanService, CyclePlanService>();
        services.AddSingleton<ICyclePlanSeedService, CyclePlanSeedService>();
        services.AddSingleton<ISeedStep, CyclePlanSeedStep>();
        return services;
    }

    public static IEndpointRouteBuilder MapCyclePlans(this IEndpointRouteBuilder routes) => routes.MapCyclePlanEndpoints();
}

/// <summary>Creates the empty active plan "Standaard" on a first start (the last step of <c>seed()</c> in apps/server/src/domain/seed.ts). Idempotent; a failure stops the start.</summary>
internal sealed partial class CyclePlanSeedStep(ICyclePlanSeedService seeding, ILogger<CyclePlanSeedStep> logger) : ISeedStep
{
    public string Name => "cycle plans";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await seeding.SeedAsync(cancellationToken);
        result.Switch(
            created =>
            {
                if (created)
                {
                    LogSeeded(logger);
                }
            },
            conflict => throw new InvalidOperationException($"Startup failed while seeding the cycle plans: {conflict.Code}"),
            error => throw new InvalidOperationException($"Startup failed while seeding the cycle plans: {error.Message}"));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded the default cycle plan")]
    private static partial void LogSeeded(ILogger logger);
}
