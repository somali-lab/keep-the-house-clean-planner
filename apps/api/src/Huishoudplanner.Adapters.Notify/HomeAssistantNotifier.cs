using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Huishoudplanner.Domain.Notifications;

namespace Huishoudplanner.Adapters.Notify;

/// <summary>Home Assistant: JSON POST to a webhook URL; the automation decides what to do with it.</summary>
internal sealed class HomeAssistantNotifier(NotifyEndpoint endpoint, IHttpClientFactory clients)
    : HttpNotifier("homeassistant", endpoint, clients)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    protected override HttpContent CreateContent(NotifyMessage message)
    {
        // title and message first, then the data fields (which may override them, as the object spread of the Node server does).
        var body = new Dictionary<string, object?> { ["title"] = message.Title, ["message"] = message.Body };
        foreach (var (key, value) in message.Data)
        {
            body[key] = value;
        }

        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}
