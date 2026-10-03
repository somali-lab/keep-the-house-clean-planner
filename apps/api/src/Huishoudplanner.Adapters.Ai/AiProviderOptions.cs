using System.Globalization;
using System.Text;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>The provider types of requirements section 5.3. The wire names are those of the Node settings.</summary>
public enum AiProviderType
{
    None,
    Mock,
    Anthropic,
    OpenAiCompatible,
    Ollama,
}

/// <summary>
/// What the factory needs to build a provider. <see cref="ApiKey"/> is the secret from <c>AI_API_KEY</c>: it is left
/// out of <see cref="ToString"/> and of the record's printed members, and no adapter puts it in an error, a log line
/// or a span.
/// </summary>
public sealed record AiProviderOptions(
    AiProviderType Type,
    string? BaseUrl = null,
    string? Model = null,
    string? ApiKey = null,
    TimeSpan? Timeout = null)
{
    public const int DefaultTimeoutSeconds = 180;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    public static string WireName(AiProviderType type) => type switch
    {
        AiProviderType.None => "none",
        AiProviderType.Mock => "mock",
        AiProviderType.Anthropic => "anthropic",
        AiProviderType.OpenAiCompatible => "openai-compatible",
        AiProviderType.Ollama => "ollama",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Parses the settings value (<c>openai-compatible</c> and so on); null when unknown.</summary>
    public static AiProviderType? ParseType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => AiProviderType.None,
        "mock" => AiProviderType.Mock,
        "anthropic" => AiProviderType.Anthropic,
        "openai-compatible" => AiProviderType.OpenAiCompatible,
        "ollama" => AiProviderType.Ollama,
        _ => null,
    };

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Type = {Type}, HasBaseUrl = {BaseUrl is not null}, Model = {Model}, HasApiKey = {!string.IsNullOrEmpty(ApiKey)}, Timeout = {Timeout}");
        return true;
    }
}
