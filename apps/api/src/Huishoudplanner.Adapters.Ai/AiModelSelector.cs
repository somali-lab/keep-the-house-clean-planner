using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>
/// <see cref="ForSelectingAModel"/> over <see cref="AiProviderFactory"/>: builds the provider from the settings of every call, so a settings
/// change (or a connection test of settings that are not stored) takes effect immediately. The factory is cheap and all providers share one
/// <c>SocketsHttpHandler</c>, so nothing is cached. The API key is read from <paramref name="apiKey"/> (the <c>AI_API_KEY</c> variable) on
/// every call and never leaves this class except inside the provider it builds.
/// </summary>
public sealed class AiModelSelector(Func<string?> apiKey, AiProviderHooks? hooks = null) : ForSelectingAModel
{
    public ForChattingWithAModel ChooseFor(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = new AiProviderOptions(
            ToType(settings.Type),
            settings.Endpoint,
            settings.Model,
            apiKey(),
            settings.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
        return AiProviderFactory.Create(options, hooks);
    }

    private static AiProviderType ToType(Domain.Settings.AiProviderType type) => type switch
    {
        Domain.Settings.AiProviderType.None => AiProviderType.None,
        Domain.Settings.AiProviderType.Mock => AiProviderType.Mock,
        Domain.Settings.AiProviderType.Anthropic => AiProviderType.Anthropic,
        Domain.Settings.AiProviderType.OpenAiCompatible => AiProviderType.OpenAiCompatible,
        Domain.Settings.AiProviderType.Ollama => AiProviderType.Ollama,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
