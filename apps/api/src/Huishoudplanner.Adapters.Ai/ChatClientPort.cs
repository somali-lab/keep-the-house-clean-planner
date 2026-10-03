using System.Diagnostics;
using System.Globalization;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.AI;
using OneOf;
using AiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using DomainChatOptions = Huishoudplanner.Domain.Ai.ChatOptions;
using DomainChatRole = Huishoudplanner.Domain.Ai.ChatRole;
using DomainChatRequest = Huishoudplanner.Domain.Ai.ChatRequest;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>
/// <see cref="ForChattingWithAModel"/> over any <see cref="IChatClient"/>: sends the messages, bounds every call by
/// the timeout, and turns infrastructure failures into value-free <see cref="PortError"/> messages
/// ("&lt;label&gt; returned HTTP 401", "&lt;label&gt; could not be reached", "&lt;label&gt; returned no content").
/// One span and one counter increment per call.
/// </summary>
internal sealed class ChatClientPort(
    string provider,
    string label,
    IChatClient client,
    Func<DomainChatOptions, AiChatOptions> mapOptions,
    TimeSpan defaultTimeout) : ForChattingWithAModel
{
    public string Provider => provider;

    public async Task<OneOf<ChatReply, AiUnavailable, PortError>> ChatAsync(DomainChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeout = request.Options.Timeout ?? defaultTimeout;
        using var activity = AiTelemetry.Source.StartActivity("ai.chat", ActivityKind.Client);
        activity?.SetTag("ai.provider", provider);
        activity?.SetTag("ai.request", request.Options.Name);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            var response = await client.GetResponseAsync(ToMessages(request), mapOptions(request.Options), bounded.Token).ConfigureAwait(false);
            var text = response.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return Fail(activity, "no_content", $"{label} returned no content");
            }

            AiTelemetry.Record(provider, "ok");
            activity?.SetTag("ai.outcome", "ok");
            return new ChatReply(text);
        }
        catch (Exception ex) when (AiFailures.IsInfrastructure(ex, cancellationToken))
        {
            // Only the status or the kind of failure is reported: SDK messages may contain request details.
            if (ex is NoContentException)
            {
                return Fail(activity, "no_content", $"{label} returned no content");
            }

            if (AiFailures.StatusOf(ex) is { } status)
            {
                return Fail(activity, "http_error", $"{label} returned HTTP {status.ToString(CultureInfo.InvariantCulture)}");
            }

            if (AiFailures.IsTimeout(ex))
            {
                return Fail(activity, "timeout", $"{label} timed out after {Math.Round(timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)} seconds");
            }

            return Fail(activity, "unreachable", ex is ModelUnavailableException ? ex.Message : $"{label} could not be reached");
        }
    }

    private PortError Fail(Activity? activity, string outcome, string message)
    {
        AiTelemetry.Record(provider, outcome);
        activity?.SetTag("ai.outcome", outcome);
        activity?.SetStatus(ActivityStatusCode.Error);
        return new PortError(message);
    }

    private static List<ChatMessage> ToMessages(DomainChatRequest request)
    {
        var messages = new List<ChatMessage>(request.Messages.Count + 1);
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new ChatMessage(Microsoft.Extensions.AI.ChatRole.System, request.SystemPrompt));
        }

        foreach (var turn in request.Messages)
        {
            messages.Add(new ChatMessage(
                turn.Role == DomainChatRole.User ? Microsoft.Extensions.AI.ChatRole.User : Microsoft.Extensions.AI.ChatRole.Assistant,
                turn.Content));
        }

        return messages;
    }
}
