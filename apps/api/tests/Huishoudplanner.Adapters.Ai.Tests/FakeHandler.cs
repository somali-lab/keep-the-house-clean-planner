using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Huishoudplanner.Adapters.Ai.Tests;

/// <summary>One request as the fake server saw it.</summary>
internal sealed record SeenRequest(HttpMethod Method, Uri Uri, HttpRequestHeaders Headers, string ContentType, string Body);

/// <summary>An in-process stand-in for a provider's HTTP endpoint: records requests, answers from a script.</summary>
internal sealed class FakeHandler(Func<SeenRequest, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    public static FakeHandler Json(HttpStatusCode status, string json) =>
        new((_, _) => Task.FromResult(Reply(status, json)));

    public static FakeHandler Throwing(Exception exception) =>
        new((_, _) => throw exception);

    public static FakeHandler NeverAnswering() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(HttpStatusCode.OK, "{}");
        });

    public static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var seen = new SeenRequest(request.Method, request.RequestUri!, request.Headers, request.Content?.Headers.ContentType?.MediaType ?? string.Empty, body);
        lock (Requests)
        {
            Requests.Add(seen);
        }

        return await respond(seen, cancellationToken);
    }
}
