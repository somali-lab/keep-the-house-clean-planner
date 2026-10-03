using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;
using AiFormat = Microsoft.Extensions.AI.ChatResponseFormat;
using AiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using DomainChatOptions = Huishoudplanner.Domain.Ai.ChatOptions;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>Seams for tests: a fake HTTP handler for the wire formats and the mock's answers. Production passes none.</summary>
public sealed record AiProviderHooks(
    HttpMessageHandler? Handler = null,
    IReadOnlyDictionary<string, MockResponder>? MockResponders = null,
    bool MockInvalidFirst = false);

/// <summary>
/// Picks the provider from the settings. Incomplete configuration is not an exception: it becomes a port that answers
/// <see cref="AiUnavailableReason.Misconfigured"/> with a message that never contains a configured value (the API key
/// included). Every provider is an <see cref="IChatClient"/> behind the same bounded, failure-mapping port.
/// </summary>
public static class AiProviderFactory
{
    /// <summary>Used when settings name no Anthropic model (as in the Node server).</summary>
    public const string DefaultAnthropicModel = "claude-opus-5";

    public const string DefaultAnthropicEndpoint = "https://api.anthropic.com";

    public const string DefaultOllamaEndpoint = "http://localhost:11434";

    public const int DefaultMaxTokens = 8192;

    private const string InvalidUrl = "The AI endpoint is not a valid URL";

    // One shared handler: an HttpClient handler per provider would exhaust sockets when the settings change at runtime.
    private static readonly SocketsHttpHandler SharedHandler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    public static ForChattingWithAModel Create(AiProviderOptions options, AiProviderHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var timeout = options.Timeout ?? AiProviderOptions.DefaultTimeout;
        return options.Type switch
        {
            AiProviderType.None => UnavailableChat.Disabled(),
            AiProviderType.Mock => CreateMock(hooks, timeout),
            AiProviderType.Anthropic => CreateAnthropic(options, hooks, timeout),
            AiProviderType.OpenAiCompatible => CreateOpenAi(options, hooks, timeout),
            AiProviderType.Ollama => CreateOllama(options, hooks, timeout),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private static ChatClientPort CreateMock(AiProviderHooks? hooks, TimeSpan timeout) =>
        new("mock", "Mock provider",
            new MockChatClient(hooks?.MockResponders ?? DefaultMockResponders.Responders, hooks?.MockInvalidFirst ?? false),
            o => new AiChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { [MockChatClient.RequestNameKey] = o.Name ?? string.Empty },
            },
            timeout);

    private static ForChattingWithAModel CreateAnthropic(AiProviderOptions options, AiProviderHooks? hooks, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(options.ApiKey))
        {
            return Misconfigured(options, "AI_API_KEY is not set");
        }

        var model = options.Model ?? DefaultAnthropicModel;
        if (!string.IsNullOrWhiteSpace(options.BaseUrl) && !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
        {
            return Misconfigured(options, InvalidUrl);
        }

        return new ChatClientPort("anthropic", "Anthropic API", AnthropicClientFor(options, hooks).AsIChatClient(model), o => Map(o, DefaultMaxTokens), timeout);
    }

    private static ForChattingWithAModel CreateOpenAi(AiProviderOptions options, AiProviderHooks? hooks, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.Model))
        {
            return Misconfigured(options, "An endpoint and model are required for an OpenAI-compatible provider");
        }

        if (!Uri.TryCreate(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var endpoint))
        {
            return Misconfigured(options, InvalidUrl);
        }

        var hasKey = !string.IsNullOrEmpty(options.ApiKey);
        var clientOptions = OpenAiClientOptions(endpoint, NewHttpClient(hooks, keepAuthorization: hasKey));
        // Without a key the handler strips the Authorization header, so a keyless local server is not sent a bogus token.
        var chat = new ChatClient(options.Model, new ApiKeyCredential(hasKey ? options.ApiKey! : "not-configured"), clientOptions);
        return new ChatClientPort("openai-compatible", "AI provider", new NoContentGuard(chat.AsIChatClient()),
            o =>
            {
                var mapped = Map(o, null);
                mapped.ResponseFormat = AiFormat.Json;
                return mapped;
            },
            timeout);
    }

    private static ForChattingWithAModel CreateOllama(AiProviderOptions options, AiProviderHooks? hooks, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            return Misconfigured(options, "A model is required for Ollama");
        }

        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? DefaultOllamaEndpoint : options.BaseUrl;
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var endpoint))
        {
            return Misconfigured(options, InvalidUrl);
        }

        var http = NewHttpClient(hooks, keepAuthorization: false);
        http.BaseAddress = endpoint;
        var client = new OllamaApiClient(http, options.Model);
        return new ChatClientPort("ollama", "Ollama", client,
            o =>
            {
                // Deterministic, long enough for a four week plan, no hidden reasoning: as the Node provider.
                var mapped = Map(o, DefaultMaxTokens);
                mapped.Temperature ??= 0;
                mapped.ResponseFormat = o.ResponseSchema is { } schema
                    ? AiFormat.ForJsonSchema(OllamaSchema.Convert(schema))
                    : AiFormat.Json;
                mapped.RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { Think = false };
                return mapped;
            },
            timeout);
    }

    /// <summary>
    /// Everything is explicit so the SDK never reads ANTHROPIC_* variables of the host. The SDK retries once on transient
    /// failures like the Node client; it has no timeout of its own, the port's token bounds the whole call (per request).
    /// </summary>
    internal static AnthropicClient AnthropicClientFor(AiProviderOptions options, AiProviderHooks? hooks) => new()
    {
        ApiKey = options.ApiKey,
        BaseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? DefaultAnthropicEndpoint : options.BaseUrl.TrimEnd('/'),
        MaxRetries = 1,
        Timeout = Timeout.InfiniteTimeSpan,
        HttpClient = NewHttpClient(hooks, keepAuthorization: true),
    };

    /// <summary>No retries and no pipeline network timeout (System.ClientModel defaults to 100 s); the port bounds the call.</summary>
    internal static OpenAIClientOptions OpenAiClientOptions(Uri endpoint, HttpClient http) => new()
    {
        Endpoint = endpoint,
        Transport = new HttpClientPipelineTransport(http),
        RetryPolicy = new ClientRetryPolicy(0),
        NetworkTimeout = Timeout.InfiniteTimeSpan,
    };

    private static UnavailableChat Misconfigured(AiProviderOptions options, string detail) =>
        new(AiProviderOptions.WireName(options.Type), AiUnavailableReason.Misconfigured, detail);

    private static AiChatOptions Map(DomainChatOptions options, int? defaultMaxTokens) => new()
    {
        Temperature = options.Temperature is { } t ? (float)t : null,
        MaxOutputTokens = options.MaxTokens ?? defaultMaxTokens,
    };

    private static HttpClient NewHttpClient(AiProviderHooks? hooks, bool keepAuthorization)
    {
        HttpMessageHandler inner = hooks?.Handler ?? SharedHandler;
        HttpMessageHandler handler = keepAuthorization ? inner : new StripAuthorizationHandler(inner);

        // The port bounds every call itself; the default 100 second HttpClient timeout would cut slow local models short.
        return new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class StripAuthorizationHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = null;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
