using Huishoudplanner.Adapters.Http.CyclePlans;
using Huishoudplanner.Adapters.Mongo.CyclePlans;
using Huishoudplanner.Application.Activation;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.CyclePlans;

/// <summary>The wiring of the plan activation: the use cases, the activation store with its guard document and the two endpoints.</summary>
public static class ActivationComposition
{
    public static IServiceCollection AddActivation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoActivation();
        services.AddScoped<IActivationService, ActivationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapActivation(this IEndpointRouteBuilder routes) => routes.MapActivationEndpoints();
}
