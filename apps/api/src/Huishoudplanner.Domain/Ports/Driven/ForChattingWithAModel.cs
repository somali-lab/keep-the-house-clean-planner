using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Asks a language model for text. Provider-neutral: a system prompt, messages and options go in, text comes out.
/// Failures are values: <see cref="AiUnavailable"/> when AI is off or incomplete, <see cref="PortError"/> when the
/// provider failed, timed out or answered without content. Messages never contain a key, an endpoint or a response body.
/// </summary>
public interface ForChattingWithAModel
{
    /// <summary>The provider type behind this port (<c>none</c>, <c>mock</c>, <c>anthropic</c>, <c>openai-compatible</c>, <c>ollama</c>).</summary>
    string Provider { get; }

    Task<OneOf<ChatReply, AiUnavailable, PortError>> ChatAsync(ChatRequest request, CancellationToken cancellationToken);
}
