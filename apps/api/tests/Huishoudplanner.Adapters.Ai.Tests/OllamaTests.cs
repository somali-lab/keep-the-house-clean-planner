using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Ai;

namespace Huishoudplanner.Adapters.Ai.Tests;

public class OllamaTests
{
    private const string Ok = "{\"model\":\"llama3.2\",\"created_at\":\"2025-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"x\\\":1}\"},\"done\":true,\"done_reason\":\"stop\"}";

    private static AiProviderOptions Options(string url = "http://ollama:11434/") => new(AiProviderType.Ollama, url, "llama3.2");

    [Fact]
    public async Task Calls_api_chat_with_the_schema_deterministic_output_and_no_streaming()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Ok);

        var result = await Helpers.Create(Options(), handler).Ask();

        result.TextOf().Should().Be("{\"x\":1}");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("http://ollama:11434/api/chat");
        request.Headers.Authorization.Should().BeNull();
        var body = request.BodyOf();
        body.GetProperty("model").GetString().Should().Be("llama3.2");
        body.GetProperty("stream").GetBoolean().Should().BeFalse();
        body.GetProperty("think").GetBoolean().Should().BeFalse();
        body.GetProperty("format").GetRawText().Should().Be("{\"type\":\"object\"}");
        body.GetProperty("options").GetProperty("temperature").GetDouble().Should().Be(0);
        body.GetProperty("options").GetProperty("num_predict").GetInt32().Should().Be(8192);
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        messages.Select(m => m.GetProperty("role").GetString()).Should().Equal("system", "user");
        messages.Select(m => m.GetProperty("content").GetString()).Should().Equal("Je bent een planner.", "{\"tasks\":[]}");
    }

    [Fact]
    public async Task Asks_for_plain_json_when_the_request_has_no_schema()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Ok);
        var request = Helpers.Request with { Options = Helpers.Request.Options with { ResponseSchema = null } };

        await Helpers.Create(Options(), handler).Ask(request);

        handler.Requests[0].BodyOf().GetProperty("format").GetString().Should().Be("json");
    }

    [Fact]
    public async Task Without_an_endpoint_the_local_default_is_used()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Ok);

        await Helpers.Create(new AiProviderOptions(AiProviderType.Ollama, null, "m"), handler).Ask();

        handler.Requests[0].Uri.ToString().Should().Be("http://localhost:11434/api/chat");
    }

    [Fact]
    public async Task Http_errors_report_the_status()
    {
        var handler = FakeHandler.Json(HttpStatusCode.InternalServerError, "{}");

        (await Helpers.Create(Options(), handler).Ask()).ErrorOf().Should().Be("Ollama returned HTTP 500");
    }

    [Fact]
    public async Task Converts_fixed_tuples_to_a_schema_supported_by_ollama()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Ok);
        var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"rationale\":{\"type\":\"array\",\"prefixItems\":[{\"type\":\"string\",\"minLength\":1},{\"type\":\"string\",\"minLength\":1},{\"type\":\"string\",\"minLength\":1},{\"type\":\"string\",\"minLength\":1}],\"items\":false,\"minItems\":4,\"maxItems\":4}}}").RootElement.Clone();
        var request = Helpers.Request with { Options = Helpers.Request.Options with { ResponseSchema = schema } };

        await Helpers.Create(Options(), handler).Ask(request);

        var rationale = handler.Requests[0].BodyOf().GetProperty("format").GetProperty("properties").GetProperty("rationale");
        rationale.GetRawText().Should().Be("{\"type\":\"array\",\"items\":{\"type\":\"string\",\"minLength\":1},\"minItems\":4,\"maxItems\":4}");
    }

    [Fact]
    public void Tuples_with_different_item_types_become_an_any_of()
    {
        var schema = JsonDocument.Parse("{\"type\":\"array\",\"prefixItems\":[{\"type\":\"string\"},{\"type\":\"integer\"}],\"items\":false}").RootElement;

        OllamaSchema.Convert(schema).GetRawText().Should().Be("{\"type\":\"array\",\"items\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"integer\"}]}}");
    }

    [Fact]
    public async Task Empty_content_is_reported()
    {
        var handler = FakeHandler.Json(HttpStatusCode.OK, Ok.Replace("{\\\"x\\\":1}", string.Empty, StringComparison.Ordinal));

        (await Helpers.Create(Options(), handler).Ask()).ErrorOf().Should().Be("Ollama returned no content");
    }

    [Fact]
    public async Task An_unreachable_server_is_reported()
    {
        var handler = FakeHandler.Throwing(new HttpRequestException("connection refused"));

        (await Helpers.Create(Options(), handler).Ask()).ErrorOf().Should().Be("Ollama could not be reached");
    }

    [Fact]
    public async Task A_slow_model_times_out()
    {
        var request = Helpers.Request with { Options = Helpers.Request.Options with { Timeout = TimeSpan.FromMilliseconds(150) } };

        (await Helpers.Create(Options(), FakeHandler.NeverAnswering()).Ask(request)).ErrorOf().Should().StartWith("Ollama timed out after ");
    }
}
