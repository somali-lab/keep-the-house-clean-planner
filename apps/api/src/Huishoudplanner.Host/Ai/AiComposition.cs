using Huishoudplanner.Adapters.Ai;
using Huishoudplanner.Adapters.Http.Ai;
using Huishoudplanner.Application.Ai;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Huishoudplanner.Host.Ai;

/// <summary>The wiring of the AI slice: the use cases, the model selector (stored settings and <c>AI_API_KEY</c>) and the endpoints.</summary>
public static class AiComposition
{
    public static IServiceCollection AddAi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAiModelSelector(sp => sp.GetRequiredService<IOptions<AppOptions>>().Value.AiApiKey);
        services.AddScoped<IAiService, AiService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAi(this IEndpointRouteBuilder routes) => routes.MapAiEndpoints();
}
