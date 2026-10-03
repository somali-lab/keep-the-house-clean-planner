using System.Net;
using Huishoudplanner.Domain.Ai;
using Microsoft.Extensions.AI;

namespace Huishoudplanner.Adapters.Ai.Tests;

public class ReviewFixTests
{
    private sealed class Throwing(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw exception;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Openai_pipeline_has_no_network_timeout_of_its_own()
    {
        // System.ClientModel defaults to 100 s, which would cut slow local models short; the port's token bounds the call.
        AiProviderFactory.OpenAiClientOptions(new Uri("http://x/v1/"), new HttpClient())
            .NetworkTimeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void Anthropic_sdk_has_no_timeout_of_its_own()
    {
        AiProviderFactory.AnthropicClientFor(new AiProviderOptions(AiProviderType.Anthropic, null, null, Helpers.Secret), null)
            .Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task Anthropic_honours_a_per_request_timeout_longer_than_the_default()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(300, ct);
            return FakeHandler.Reply(HttpStatusCode.OK, "{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}");
        });
        var options = new AiProviderOptions(AiProviderType.Anthropic, null, "m", Helpers.Secret, TimeSpan.FromMilliseconds(100));
        var request = Helpers.Request with { Options = Helpers.Request.Options with { Timeout = TimeSpan.FromSeconds(10) } };

        (await Helpers.Create(options, handler).Ask(request)).TextOf().Should().Be("ok");
    }

    [Fact]
    public async Task Unexpected_exceptions_are_counted_as_error_and_rethrown()
    {
        var outcomes = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == AiTelemetry.Name)
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcomes.Add(tag.Value?.ToString() ?? string.Empty);
                }
            }
        });
        listener.Start();
        var port = new ChatClientPort("test", "Test", new Throwing(new InvalidOperationException("bug")), _ => new Microsoft.Extensions.AI.ChatOptions(), TimeSpan.FromSeconds(5));

        var act = () => port.ChatAsync(Helpers.Request, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        outcomes.Should().Equal("error");
    }

    [Fact]
    public async Task The_no_content_guard_leaves_foreign_out_of_range_errors_alone()
    {
        var guard = new NoContentGuard(new Throwing(new ArgumentOutOfRangeException("ours")));

        var act = () => guard.GetResponseAsync([new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "x")], null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("connect to (127.0.0.1:11434) failed")]
    [InlineData("took (500 ms) too long")]
    public async Task Numbers_in_a_transport_message_are_not_mistaken_for_a_status(string message)
    {
        var handler = FakeHandler.Throwing(new HttpRequestException(message));

        (await Helpers.Create(new AiProviderOptions(AiProviderType.Ollama, "http://o", "m"), handler).Ask()).ErrorOf().Should().Be("Ollama could not be reached");
    }
}
