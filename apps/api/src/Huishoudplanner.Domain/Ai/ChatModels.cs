using System.Text.Json;

namespace Huishoudplanner.Domain.Ai;

public enum ChatRole
{
    User,
    Assistant,
}

/// <summary>One message of the conversation sent to a model.</summary>
public sealed record ChatTurn(ChatRole Role, string Content);

/// <summary>
/// Per call settings. Everything is optional; the adapter applies its configured defaults.
/// <paramref name="Name"/> is the stable name of the requested output (see <see cref="AiRequestNames"/>); it labels
/// telemetry and selects the answer of the deterministic mock, and is never sent to a real provider.
/// <paramref name="ResponseSchema"/> is the JSON schema of the expected answer for providers that can constrain
/// generation with it (Ollama); the others rely on the prompt.
/// </summary>
public sealed record ChatOptions(
    string? Name = null,
    double? Temperature = null,
    int? MaxTokens = null,
    TimeSpan? Timeout = null,
    JsonElement? ResponseSchema = null);

/// <summary>A system prompt, the conversation so far and the options of this call.</summary>
public sealed record ChatRequest(string SystemPrompt, IReadOnlyList<ChatTurn> Messages, ChatOptions Options)
{
    public ChatRequest(string systemPrompt, string userMessage, ChatOptions? options = null)
        : this(systemPrompt, [new ChatTurn(ChatRole.User, userMessage)], options ?? new ChatOptions())
    {
    }
}

/// <summary>The text a model answered with (never empty).</summary>
public sealed record ChatReply(string Text);

/// <summary>Why no model could be asked at all.</summary>
public enum AiUnavailableReason
{
    /// <summary>The provider type is <c>none</c>. Maps to <c>503 ai_disabled</c>.</summary>
    Disabled,

    /// <summary>The settings or environment are incomplete, for example no API key. Maps to <c>503 ai_misconfigured</c>.</summary>
    Misconfigured,
}

/// <summary>AI is switched off or incomplete. <paramref name="Detail"/> never contains a configured value.</summary>
public sealed record AiUnavailable(AiUnavailableReason Reason, string Detail);

/// <summary>The stable names of the AI outputs a use case asks for.</summary>
public static class AiRequestNames
{
    public const string PlanProposal = "plan-proposal";
    public const string ConnectionTest = "connection-test";
    public const string TaskSuggestions = "task-suggestions";
    public const string PlanExplanation = "plan-explanation";
}
