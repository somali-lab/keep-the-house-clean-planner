using System.Net.Http.Headers;
using System.Text;
using Huishoudplanner.Domain.Notifications;

namespace Huishoudplanner.Adapters.Notify;

/// <summary>ntfy: plain-text POST to the topic URL (NOTIFY_URL), optional bearer token.</summary>
internal sealed class NtfyNotifier(NotifyEndpoint endpoint, IHttpClientFactory clients) : HttpNotifier("ntfy", endpoint, clients)
{
    protected override HttpContent CreateContent(NotifyMessage message)
    {
        var content = new StringContent(message.Body, Encoding.UTF8, new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" });
        content.Headers.Add("Title", AsciiHeader(message.Title));
        content.Headers.Add("Tags", "broom");
        return content;
    }

    /// <summary>HTTP header values must be ASCII; names and Dutch text go in the UTF-8 body.</summary>
    internal static string AsciiHeader(string value) =>
        new(value.Normalize(NormalizationForm.FormKD).Where(c => c is >= ' ' and <= '~').ToArray());
}
