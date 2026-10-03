using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Huishoudplanner.Domain.Ai;
using AiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>What the mock sees of a request: the requested output name, the system prompt and the last user message.</summary>
public sealed record MockRequest(string Name, string System, string User);

/// <summary>
/// Answers one request name. Fixed strings are served in order (the last one repeats); a function gets the
/// 0-based attempt number.
/// </summary>
public sealed class MockResponder
{
    private readonly string[]? fixedAnswers;
    private readonly Func<MockRequest, int, string>? function;

    private MockResponder(string[]? fixedAnswers, Func<MockRequest, int, string>? function)
    {
        this.fixedAnswers = fixedAnswers;
        this.function = function;
    }

    public static MockResponder Fixed(params string[] answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        return new MockResponder(answers, null);
    }

    public static MockResponder From(Func<MockRequest, int, string> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new MockResponder(null, function);
    }

    internal string? Answer(MockRequest request, int attempt, int skipped)
    {
        if (function is not null)
        {
            return function(request, attempt);
        }

        if (fixedAnswers is not { Length: > 0 })
        {
            return null;
        }

        return fixedAnswers[Math.Max(0, Math.Min(attempt - skipped, fixedAnswers.Length - 1))];
    }
}

/// <summary>
/// Deterministic <see cref="IChatClient"/> for tests and demos; never calls the network. Port of the Node
/// <c>MockProvider</c>: answers per request name, optionally answering invalid JSON first to exercise the re-prompt.
/// </summary>
public sealed class MockChatClient(IReadOnlyDictionary<string, MockResponder> responders, bool invalidFirst = false) : IChatClient
{
    public const string InvalidResponse = "this is not json";

    /// <summary>The additional property that carries the request name from the port to the mock.</summary>
    public const string RequestNameKey = "huishoudplanner.request-name";

    private readonly Lock gate = new();
    private readonly Dictionary<string, int> attempts = [];
    private readonly List<MockRequest> requests = [];

    /// <summary>Every request received, in order.</summary>
    public IReadOnlyList<MockRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, AiChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages.ToList();
        var name = options?.AdditionalProperties?.TryGetValue(RequestNameKey, out var value) == true ? value as string ?? string.Empty : string.Empty;
        var request = new MockRequest(
            name,
            string.Join("\n", list.Where(m => m.Role == AiChatRole.System).Select(m => m.Text)),
            list.LastOrDefault(m => m.Role == AiChatRole.User)?.Text ?? string.Empty);

        int attempt;
        lock (gate)
        {
            attempt = attempts.GetValueOrDefault(request.Name);
            attempts[request.Name] = attempt + 1;
            requests.Add(request);
        }

        if (invalidFirst && attempt == 0)
        {
            return Task.FromResult(Reply(InvalidResponse));
        }

        if (!responders.TryGetValue(request.Name, out var responder))
        {
            throw new ModelUnavailableException(string.Create(CultureInfo.InvariantCulture, $"Mock provider has no response for \"{request.Name}\""));
        }

        var answer = responder.Answer(request, attempt, invalidFirst ? 1 : 0)
            ?? throw new ModelUnavailableException(string.Create(CultureInfo.InvariantCulture, $"Mock provider has no response for \"{request.Name}\""));
        return Task.FromResult(Reply(answer));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, AiChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The mock chat client does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static ChatResponse Reply(string text) => new(new ChatMessage(AiChatRole.Assistant, text));
}
