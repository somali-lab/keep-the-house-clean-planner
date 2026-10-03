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

    /// <summary>
    /// Registers <see cref="ForSelectingAModel"/>: the provider is built from the stored settings on every call, with the key
    /// from <paramref name="apiKey"/> (resolved lazily, after configuration is bound), so a settings change applies immediately.
    /// </summary>
    public static IServiceCollection AddAiModelSelector(this IServiceCollection services, Func<IServiceProvider, string?> apiKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(apiKey);
        services.TryAddSingleton<ForSelectingAModel>(sp => new AiModelSelector(() => apiKey(sp)));
        return services;
    }
}
