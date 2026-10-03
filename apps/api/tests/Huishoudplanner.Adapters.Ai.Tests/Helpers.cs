using System.Text.Json;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Adapters.Ai.Tests;

internal static class Helpers
{
    public const string Secret = "sk-test-super-secret-key";

    public static readonly ChatRequest Request = new(
        "Je bent een planner.",
        "{\"tasks\":[]}",
        new ChatOptions(Name: AiRequestNames.PlanProposal, ResponseSchema: JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()));

    public static ForChattingWithAModel Create(AiProviderOptions options, FakeHandler? handler = null, IReadOnlyDictionary<string, MockResponder>? responders = null, bool invalidFirst = false) =>
        AiProviderFactory.Create(options, new AiProviderHooks(handler, responders, invalidFirst));

    public static Task<OneOf<ChatReply, AiUnavailable, PortError>> Ask(this ForChattingWithAModel port, ChatRequest? request = null) =>
        port.ChatAsync(request ?? Request, TestContext.Current.CancellationToken);

    public static string TextOf(this OneOf<ChatReply, AiUnavailable, PortError> result) => result.AsT0.Text;

    public static string ErrorOf(this OneOf<ChatReply, AiUnavailable, PortError> result)
    {
        result.IsT2.Should().BeTrue("the call should have failed with a PortError but was {0}", result.Value);
        return result.AsT2.Message;
    }

    public static JsonElement BodyOf(this SeenRequest request) => JsonDocument.Parse(request.Body).RootElement.Clone();
}
