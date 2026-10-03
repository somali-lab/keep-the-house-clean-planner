using Huishoudplanner.Domain.Ports.Driven;

namespace Huishoudplanner.Adapters.Notify;

internal static class NotifierFactory
{
    /// <summary>The none notifier also stands in when a URL is missing (the Host options validation already refuses that).</summary>
    public static ForSendingNotifications Create(NotifyEndpoint endpoint, IHttpClientFactory clients)
    {
        if (endpoint.Kind == NotifyKind.None || string.IsNullOrEmpty(endpoint.Url))
        {
            return new NoneNotifier();
        }

        return endpoint.Kind == NotifyKind.Ntfy
            ? new NtfyNotifier(endpoint, clients)
            : new HomeAssistantNotifier(endpoint, clients);
    }
}
