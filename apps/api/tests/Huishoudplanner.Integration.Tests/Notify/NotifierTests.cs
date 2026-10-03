using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Huishoudplanner.Adapters.Notify;
using Huishoudplanner.Domain.Notifications;
using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;

namespace Huishoudplanner.Integration.Tests.Notify;

/// <summary>The notifiers section of apps/server/test/notify.test.ts, against a stubbed HTTP handler (never a real endpoint).</summary>
public sealed class NotifierTests
{
    private const string Token = "tok-very-secret";

    private static readonly NotifyMessage Message = new(
        "Keep the House Clean", "Goedemorgen Zoë!", new Dictionary<string, object?> { ["kind"] = "morning", ["openToday"] = 2 });

    private sealed record Captured(HttpMethod Method, Uri? Url, Dictionary<string, string> Headers, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Captured> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Captured(request.Method, request.RequestUri, headers, body));
            return await respond(request, cancellationToken);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static StubHandler Ok() => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));

    private static StubHandler Status(HttpStatusCode code) => new((_, _) => Task.FromResult(new HttpResponseMessage(code)));

    private static StubHandler Hanging() => new(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    private static ForSendingNotifications Build(NotifyKind kind, StubHandler handler, string url = "https://ntfy.example/huis", string? token = Token, TimeSpan? timeout = null) =>
        NotifierFactory.Create(new NotifyEndpoint(kind, url, token, timeout), new StubFactory(handler));

    [Fact]
    public async Task None_isDisabled_andSucceedsWithoutSending()
    {
        var handler = Ok();
        var notifier = NotifierFactory.Create(new NotifyEndpoint(NotifyKind.None, "https://x.example", null), new StubFactory(handler));

        notifier.IsEnabled.Should().BeFalse();
        (await notifier.SendAsync(Message, TestContext.Current.CancellationToken)).IsT0.Should().BeTrue();
        handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Ntfy_postsPlainTextToTheTopicUrl_withBearerToken_andAsciiHeaders()
    {
        var handler = Ok();
        var notifier = Build(NotifyKind.Ntfy, handler);
        notifier.IsEnabled.Should().BeTrue();

        var result = await notifier.SendAsync(Message with { Title = "Keep the House Clean · Zoë" }, TestContext.Current.CancellationToken);

        result.IsT0.Should().BeTrue();
        var call = handler.Calls.Should().ContainSingle().Subject;
        call.Method.Should().Be(HttpMethod.Post);
        call.Url!.ToString().Should().Be("https://ntfy.example/huis");
        call.Headers["Authorization"].Should().Be($"Bearer {Token}");
        call.Headers["Title"].Should().Be("Keep the House Clean  Zoe");
        call.Headers["Tags"].Should().Be("broom");
        call.Headers["Content-Type"].Should().StartWith("text/plain");
        call.Body.Should().Be("Goedemorgen Zoë!");
    }

    [Theory]
    [InlineData(NotifyKind.Ntfy)]
    [InlineData(NotifyKind.HomeAssistant)]
    public async Task NoToken_sendsNoAuthorizationHeader(NotifyKind kind)
    {
        var handler = Ok();
        await Build(kind, handler, token: null).SendAsync(Message, TestContext.Current.CancellationToken);
        handler.Calls.Single().Headers.Should().NotContainKey("Authorization");
    }

    [Fact]
    public async Task HomeAssistant_postsJsonWithTheStructuredFields()
    {
        var handler = Ok();
        var result = await Build(NotifyKind.HomeAssistant, handler, "http://ha.local:8123/api/webhook/huis")
            .SendAsync(Message, TestContext.Current.CancellationToken);

        result.IsT0.Should().BeTrue();
        var call = handler.Calls.Single();
        call.Url!.ToString().Should().Be("http://ha.local:8123/api/webhook/huis");
        call.Headers["Authorization"].Should().Be($"Bearer {Token}");
        call.Headers["Content-Type"].Should().Be("application/json");
        using var json = JsonDocument.Parse(call.Body);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("title", "message", "kind", "openToday");
        json.RootElement.GetProperty("title").GetString().Should().Be("Keep the House Clean");
        json.RootElement.GetProperty("message").GetString().Should().Be("Goedemorgen Zoë!");
        json.RootElement.GetProperty("kind").GetString().Should().Be("morning");
        json.RootElement.GetProperty("openToday").GetInt32().Should().Be(2);
    }

    [Theory]
    [InlineData(NotifyKind.Ntfy)]
    [InlineData(NotifyKind.HomeAssistant)]
    public async Task HttpError_isAPortError_withoutLeakingUrlOrToken(NotifyKind kind)
    {
        var result = await Build(kind, Status(HttpStatusCode.InternalServerError), "https://ntfy.example/secret-topic")
            .SendAsync(Message, TestContext.Current.CancellationToken);

        var error = result.AsT1;
        error.Message.Should().Contain("500");
        error.Message.Should().NotContain(Token).And.NotContain("secret-topic");
    }

    [Theory]
    [InlineData(NotifyKind.Ntfy)]
    [InlineData(NotifyKind.HomeAssistant)]
    public async Task NetworkFailure_isAPortError_withoutLeakingUrlOrToken(NotifyKind kind)
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException($"connect failed https://ntfy.example/secret-topic {Token}"));
        var result = await Build(kind, handler, "https://ntfy.example/secret-topic").SendAsync(Message, TestContext.Current.CancellationToken);

        var error = result.AsT1;
        error.Message.Should().Contain("request failed");
        error.Message.Should().NotContain(Token).And.NotContain("secret-topic");
    }

    [Fact]
    public async Task Timeout_isBounded_andReportedAsAPortError()
    {
        var result = await Build(NotifyKind.Ntfy, Hanging(), timeout: TimeSpan.FromMilliseconds(50))
            .SendAsync(Message, TestContext.Current.CancellationToken);

        result.AsT1.Message.Should().Contain("request failed");
    }

    [Fact]
    public async Task CallerCancellation_isAPortError_notAnException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Build(NotifyKind.Ntfy, Hanging()).SendAsync(Message, cts.Token);

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Deliveries_areCounted_byNotifierAndOutcome()
    {
        var measurements = new List<(string? Notifier, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == NotifyTelemetry.Name && instrument.Name == NotifyTelemetry.DeliveriesName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? notifier = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "notifier") { notifier = tag.Value as string; }
                if (tag.Key == "outcome") { outcome = tag.Value as string; }
            }

            lock (measurements) { measurements.Add((notifier, outcome)); }
        });
        listener.Start();

        await Build(NotifyKind.Ntfy, Ok()).SendAsync(Message, TestContext.Current.CancellationToken);
        await Build(NotifyKind.HomeAssistant, Status(HttpStatusCode.BadGateway)).SendAsync(Message, TestContext.Current.CancellationToken);

        lock (measurements)
        {
            measurements.Should().Contain(("ntfy", "success"));
            measurements.Should().Contain(("homeassistant", "failure"));
        }
    }

    [Theory]
    [InlineData(NotifyKind.None, false)]
    [InlineData(NotifyKind.Ntfy, true)]
    [InlineData(NotifyKind.HomeAssistant, true)]
    public void Registration_selectsTheNotifierByKind(NotifyKind kind, bool enabled)
    {
        using var provider = new ServiceCollection()
            .AddNotifyAdapter(_ => new NotifyEndpoint(kind, "https://ntfy.example/huis", null))
            .BuildServiceProvider();

        provider.GetRequiredService<ForSendingNotifications>().IsEnabled.Should().Be(enabled);
    }
}
