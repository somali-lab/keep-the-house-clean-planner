using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Ai;

namespace Huishoudplanner.Adapters.Ai.Tests;

public class OpenAiCompatibleTests
{
    private const string Ok = "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-x\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"slots\\\":[]}\"},\"finish_reason\":\"stop\"}]}";

    private static AiProviderOptions Options(string? key = Helpers.Secret, string url = "https://llm.example/v1/") =>
        new(AiProviderType.OpenAiCompatible, url, "gpt-x", key);

    [Fact]
    public async Task Posts_a_chat_completion_asking_for_a_json_object_and_returns_the_message_content()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Ok);

        var result = await Helpers.Create(Options(), handler).Ask();

        result.TextOf().Should().Be("{\"slots\":[]}");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://llm.example/v1/chat/completions");
        request.ContentType.Should().Be("application/json");
        request.Headers.Authorization!.ToString().Should().Be($"Bearer {Helpers.Secret}");
        var body = request.BodyOf();
        body.GetProperty("model").GetString().Should().Be("gpt-x");
        body.GetProperty("response_format").GetProperty("type").GetString().Should().Be("json_object");
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().HaveCount(2);
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").ToString().Should().Contain("Je bent een planner.");
        messages[1].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("content").ToString().Should().Contain("{\"tasks\":[]}");
    }

    [Fact]
    public async Task Passes_temperature_and_max_tokens_when_asked()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Ok);
        var request = Helpers.Request with { Options = Helpers.Request.Options with { Temperature = 0.2, MaxTokens = 123 } };

        await Helpers.Create(Options(), handler).Ask(request);

        var body = handler.Requests[0].BodyOf();
        body.GetProperty("temperature").GetDouble().Should().BeApproximately(0.2, 0.0001);
        (body.TryGetProperty("max_completion_tokens", out var max) ? max : body.GetProperty("max_tokens")).GetInt32().Should().Be(123);
    }

    [Fact]
    public async Task Http_errors_become_a_value_free_port_error()
    {
        var handler = FakeHandler.Json(HttpStatusCode.Unauthorized, $"{{\"error\":{{\"message\":\"bad key {Helpers.Secret}\"}}}}");

        var message = (await Helpers.Create(Options(), handler).Ask()).ErrorOf();

        message.Should().Be("AI provider returned HTTP 401").And.NotContain(Helpers.Secret);
    }

    [Fact]
    public async Task Server_errors_report_the_status()
    {
        var handler = FakeHandler.Json(HttpStatusCode.InternalServerError, "{}");

        (await Helpers.Create(Options(), handler).Ask()).ErrorOf().Should().Be("AI provider returned HTTP 500");
    }

    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":\"stop\"}]}")]
    public async Task An_answer_without_content_is_reported(string body)
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, body);

        (await Helpers.Create(Options(), handler).Ask()).ErrorOf().Should().Be("AI provider returned no content");
    }

    [Fact]
    public async Task A_malformed_body_is_a_port_error_not_an_exception()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, "<html>gateway</html>");

        var message = (await Helpers.Create(Options(), handler).Ask()).ErrorOf();

        message.Should().StartWith("AI provider ").And.NotContain("gateway").And.NotContain(Helpers.Secret);
    }

    [Fact]
    public async Task An_unreachable_endpoint_is_reported_without_the_key()
    {
        var handler = FakeHandler.Throwing(new HttpRequestException($"connect ECONNREFUSED {Helpers.Secret}"));

        (await Helpers.Create(Options(), handler).Ask()).ErrorOf().Should().Be("AI provider could not be reached");
    }

    [Fact]
    public async Task Every_call_is_bounded_by_the_timeout()
    {
        var request = Helpers.Request with { Options = Helpers.Request.Options with { Timeout = TimeSpan.FromMilliseconds(150) } };

        var message = (await Helpers.Create(Options(), FakeHandler.NeverAnswering()).Ask(request)).ErrorOf();

        message.Should().StartWith("AI provider timed out after ");
    }

    [Fact]
    public async Task The_configured_timeout_applies_when_the_request_names_none()
    {
        var options = Options() with { Timeout = TimeSpan.FromMilliseconds(150) };

        (await Helpers.Create(options, FakeHandler.NeverAnswering()).Ask()).ErrorOf().Should().StartWith("AI provider timed out after ");
    }

    [Fact]
    public async Task Cancelling_the_call_propagates_instead_of_becoming_a_port_error()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var port = Helpers.Create(Options(), FakeHandler.NeverAnswering());

        var act = () => port.ChatAsync(Helpers.Request, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Without_a_key_no_Authorization_header_is_sent(string? key)
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Ok);

        await Helpers.Create(Options(key, "http://local/v1"), handler).Ask();

        handler.Requests[0].Uri.ToString().Should().Be("http://local/v1/chat/completions");
        handler.Requests[0].Headers.Authorization.Should().BeNull();
    }
}
