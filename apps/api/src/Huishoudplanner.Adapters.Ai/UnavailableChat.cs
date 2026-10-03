using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>
/// The port when no model can be asked: provider <c>none</c> (switched off, <see cref="AiUnavailableReason.Disabled"/>)
/// or incomplete settings (<see cref="AiUnavailableReason.Misconfigured"/>). Never calls anything; the rest of the
/// application is unaffected.
/// </summary>
public sealed class UnavailableChat(string provider, AiUnavailableReason reason, string detail) : ForChattingWithAModel
{
    public const string DisabledDetail = "The AI assistant is disabled";

    public string Provider => provider;

    public static UnavailableChat Disabled() => new("none", AiUnavailableReason.Disabled, DisabledDetail);

    public Task<OneOf<ChatReply, AiUnavailable, PortError>> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        AiTelemetry.Record(provider, "unavailable");
        return Task.FromResult<OneOf<ChatReply, AiUnavailable, PortError>>(new AiUnavailable(reason, detail));
    }
}
