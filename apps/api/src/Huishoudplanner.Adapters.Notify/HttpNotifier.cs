using System.Diagnostics;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Notifications;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Adapters.Notify;

/// <summary>
/// POSTs to the configured URL and maps every failure to a <see cref="PortError"/>. The message holds the notifier
/// and the HTTP status only: an ntfy topic URL is effectively a secret, and the token must never appear anywhere.
/// </summary>
internal abstract class HttpNotifier(string type, NotifyEndpoint endpoint, IHttpClientFactory clients) : ForSendingNotifications
{
    public const string ClientName = "huishoudplanner-notify";

    public bool IsEnabled => true;

    protected abstract HttpContent CreateContent(NotifyMessage message);

    public async Task<OneOf<Success, PortError>> SendAsync(NotifyMessage message, CancellationToken cancellationToken)
    {
        using var activity = NotifyTelemetry.Source.StartActivity("notify.send");
        activity?.SetTag("notifier", type);
        var result = await PostAsync(message, cancellationToken);
        NotifyTelemetry.CountDelivery(type, result.IsT0);
        activity?.SetStatus(result.IsT0 ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        return result;
    }

    private async Task<OneOf<Success, PortError>> PostAsync(NotifyMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(endpoint.Timeout ?? NotifyEndpoint.DefaultTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = CreateContent(message) };
            if (!string.IsNullOrEmpty(endpoint.Token))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {endpoint.Token}");
            }

            using var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode
                ? new Success()
                : new PortError($"notify {type} failed with HTTP {(int)response.StatusCode}");
        }
#pragma warning disable CA1031 // A port never throws: every failure, including cancellation and timeout, becomes a PortError.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new PortError($"notify {type} request failed");
        }
    }
}
