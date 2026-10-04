using Huishoudplanner.Adapters.Notify;
using Huishoudplanner.Application.Notifications;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Huishoudplanner.Host.Notifications;

internal static class NotificationRegistration
{
    /// <summary>NOTIFY_TYPE, NOTIFY_URL and NOTIFY_TOKEN select the <c>ForSendingNotifications</c> adapter (none by default).</summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IMorningNotifyService, MorningNotifyService>();
        return services.AddNotifyAdapter(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AppOptions>>().Value;
            var kind = options.NotifyType switch
            {
                NotifyType.Ntfy => NotifyKind.Ntfy,
                NotifyType.HomeAssistant => NotifyKind.HomeAssistant,
                _ => NotifyKind.None,
            };
            return new NotifyEndpoint(kind, options.NotifyUrl, options.NotifyToken);
        });
    }
}
