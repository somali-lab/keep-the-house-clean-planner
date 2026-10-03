using Huishoudplanner.Adapters.Http.Points;
using Huishoudplanner.Adapters.Mongo.Points;
using Huishoudplanner.Application.Points;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Startup;

namespace Huishoudplanner.Host.Points;

/// <summary>
/// The wiring of the points ledger slice (ADR-0011): the use cases, the Mongo stores, the endpoints and the reconciliation at startup. The
/// nightly composite is a use case too (<see cref="INightlyService"/>) that the scheduler of slice 6.3 calls.
/// </summary>
public static class PointsComposition
{
    public static IServiceCollection AddPoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoPoints();
        services.AddSingleton<ReconcileGate>();
        services.AddSingleton<PointsService>();
        services.AddSingleton<IPointsService>(sp => sp.GetRequiredService<PointsService>());
        services.AddSingleton<IExecutionPointsService>(sp => sp.GetRequiredService<PointsService>());
        services.AddScoped<INightlyService, NightlyService>();
        // After every seed step: the reconciliation needs the settings and the users the seeds write (Node: reconcile after seed).
        services.AddSingleton<ISeedStep, PointsReconcileStep>();
        return services;
    }

    public static IEndpointRouteBuilder MapPoints(this IEndpointRouteBuilder routes) => routes.MapPointsEndpoints();
}

/// <summary>
/// The reconciliation of the ledger at startup, before the host accepts requests (requirements 4.12; <c>reconcilePointsSafely(ctx, 'startup')</c>):
/// on the first start after the upgrade it awards all existing history its points, every later start writes nothing. A failing run is logged and
/// never keeps the application from starting, unlike the seeds.
/// </summary>
internal sealed class PointsReconcileStep(IPointsService points, ILogger<PointsReconcileStep> logger) : ISeedStep
{
    public string Name => "points reconciliation";

    public async Task RunAsync(CancellationToken cancellationToken) =>
        await SafeReconcile.RunAsync(points, AuditActor.System, PointsRecomputeTrigger.Startup, logger, cancellationToken);
}
