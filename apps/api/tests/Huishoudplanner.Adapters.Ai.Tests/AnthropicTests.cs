using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Ai;

namespace Huishoudplanner.Adapters.Ai.Tests;

public class AnthropicTests
{
    private static string Message(string content) =>
        $"{{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"m\",\"content\":{content},\"stop_reason\":\"end_turn\",\"stop_sequence\":null,\"usage\":{{\"input_tokens\":1,\"output_tokens\":1}}}}";

    private static string Text(string text) => Message($"[{{\"type\":\"text\",\"text\":\"{text}\"}}]");

    private static AiProviderOptions Options(string? model = "claude-sonnet-5") => new(AiProviderType.Anthropic, null, model, Helpers.Secret);

    [Fact]
    public async Task Sends_system_and_user_prompt_to_the_messages_api_without_sampling_parameters()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Message("[{\"type\":\"text\",\"text\":\"{\\\"slots\\\":[]}\"}]"));

        var result = await Helpers.Create(Options(), handler).Ask();

        result.TextOf().Should().Be("{\"slots\":[]}");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://api.anthropic.com/v1/messages");
        request.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be(Helpers.Secret);
        request.Headers.Contains("anthropic-version").Should().BeTrue();
        var body = request.BodyOf();
        body.GetProperty("model").GetString().Should().Be("claude-sonnet-5");
        body.GetProperty("max_tokens").GetInt32().Should().Be(AiProviderFactory.DefaultMaxTokens);
        body.GetProperty("system").ToString().Should().Contain("Je bent een planner.");
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().ContainSingle();
        messages[0].GetProperty("role").GetString().Should().Be("user");
        messages[0].GetProperty("content").ToString().Should().Contain("tasks");
        body.TryGetProperty("temperature", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Joins_text_blocks_and_ignores_other_block_types()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Message(
            "[{\"type\":\"thinking\",\"thinking\":\"hmm\",\"signature\":\"s\"},{\"type\":\"text\",\"text\":\"{\\\"a\\\":\"},{\"type\":\"text\",\"text\":\"1}\"}]"));

        (await Helpers.Create(Options(), handler).Ask()).TextOf().Should().Be("{\"a\":1}");
    }

    [Fact]
    public async Task Api_errors_are_reported_by_status_only_without_the_key_or_the_sdk_message()
    {
        var handler = FakeHandler.Json(HttpStatusCode.Unauthorized,
            $"{{\"type\":\"error\",\"error\":{{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key {Helpers.Secret}\"}}}}");

        var message = (await Helpers.Create(Options(), handler).Ask()).ErrorOf();

        message.Should().Be("Anthropic API returned HTTP 401").And.NotContain(Helpers.Secret);
    }

    [Fact]
    public async Task An_unreachable_api_is_reported()
    {
        var handler = FakeHandler.Throwing(new HttpRequestException("socket hang up"));

        (await Helpers.Create(Options(), handler).Ask()).ErrorOf().Should().Be("Anthropic API could not be reached");
    }

    [Fact]
    public async Task An_answer_without_text_fails_clearly()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Text("   "));

        (await Helpers.Create(Options(), handler).Ask()).ErrorOf().Should().Be("Anthropic API returned no content");
    }

    [Fact]
    public async Task A_malformed_body_is_a_port_error()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, "not json at all");

        (await Helpers.Create(Options(), handler).Ask()).ErrorOf().Should().StartWith("Anthropic API ").And.NotContain(Helpers.Secret);
    }

    [Fact]
    public async Task A_slow_api_times_out()
    {
        var request = Helpers.Request with { Options = Helpers.Request.Options with { Timeout = TimeSpan.FromMilliseconds(150) } };

        (await Helpers.Create(Options(), FakeHandler.NeverAnswering()).Ask(request)).ErrorOf().Should().StartWith("Anthropic API timed out after ");
    }

    [Fact]
    public async Task Uses_the_model_from_the_settings_or_the_default()
    {
        var withModel = FakeHandler.Json(HttpStatusCode.OK, Text("{}"));
        await Helpers.Create(Options("claude-haiku-4-5-20251001"), withModel).Ask();
        withModel.Requests[0].BodyOf().GetProperty("model").GetString().Should().Be("claude-haiku-4-5-20251001");

        var withDefault = FakeHandler.Json(HttpStatusCode.OK, Text("{}"));
        await Helpers.Create(Options(null), withDefault).Ask();
        withDefault.Requests[0].BodyOf().GetProperty("model").GetString().Should().Be(AiProviderFactory.DefaultAnthropicModel);
    }

    [Fact]
    public async Task Max_tokens_from_the_request_wins_and_a_custom_base_url_is_used()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Text("{}"));
        var request = Helpers.Request with { Options = Helpers.Request.Options with { MaxTokens = 500 } };

        await Helpers.Create(Options() with { BaseUrl = "https://proxy.example/" }, handler).Ask(request);

        handler.Requests[0].Uri.ToString().Should().Be("https://proxy.example/v1/messages");
        handler.Requests[0].BodyOf().GetProperty("max_tokens").GetInt32().Should().Be(500);
    }

    [Fact]
    public void The_key_is_not_reachable_through_the_port_object()
    {
        var port = Helpers.Create(Options());

        JsonSerializer.Serialize(port, port.GetType()).Should().NotContain(Helpers.Secret);
        port.ToString().Should().NotContain(Helpers.Secret);
    }
}
