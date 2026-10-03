using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Huishoudplanner.Adapters.Ai;

public static class AiAdapterRegistration
{
    /// <summary>
    /// Registers <see cref="ForChattingWithAModel"/> for the options resolved lazily by <paramref name="options"/>
    /// (after configuration is bound). Driver-free for the caller: no provider type leaves this assembly.
    /// </summary>
    public static IServiceCollection AddAiAdapter(this IServiceCollection services, Func<IServiceProvider, AiProviderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton<ForChattingWithAModel>(sp => AiProviderFactory.Create(options(sp)));
        return services;
    }
}
