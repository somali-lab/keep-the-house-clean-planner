using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;

namespace Huishoudplanner.Adapters.Notify;

public static class NotifyAdapterRegistration
{
    /// <summary>
    /// Registers <see cref="ForSendingNotifications"/>: ntfy, Home Assistant or none, as <paramref name="endpoint"/>
    /// (resolved lazily, after configuration is bound) selects. Delivery uses a named client of the
    /// <see cref="IHttpClientFactory"/> with a bounded timeout.
    /// </summary>
    public static IServiceCollection AddNotifyAdapter(this IServiceCollection services, Func<IServiceProvider, NotifyEndpoint> endpoint)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(endpoint);

        services.AddHttpClient(HttpNotifier.ClientName);
        services.AddSingleton(sp => NotifierFactory.Create(endpoint(sp), sp.GetRequiredService<IHttpClientFactory>()));
        return services;
    }
}
