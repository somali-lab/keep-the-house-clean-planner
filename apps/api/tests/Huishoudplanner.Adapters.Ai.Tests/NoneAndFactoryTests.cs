using Huishoudplanner.Domain.Ai;

namespace Huishoudplanner.Adapters.Ai.Tests;

public class NoneAndFactoryTests
{
    [Fact]
    public async Task None_provider_answers_disabled_for_every_request()
    {
        var port = Helpers.Create(new AiProviderOptions(AiProviderType.None));

        var result = await port.Ask();

        port.Provider.Should().Be("none");
        result.IsT1.Should().BeTrue();
        result.AsT1.Should().Be(new AiUnavailable(AiUnavailableReason.Disabled, "The AI assistant is disabled"));
    }

    [Theory]
    [InlineData(AiProviderType.None, "none")]
    [InlineData(AiProviderType.Mock, "mock")]
    [InlineData(AiProviderType.Anthropic, "anthropic")]
    [InlineData(AiProviderType.OpenAiCompatible, "openai-compatible")]
    [InlineData(AiProviderType.Ollama, "ollama")]
    public void Factory_selects_the_provider_from_the_options(AiProviderType type, string expected)
    {
        var options = new AiProviderOptions(type, "https://llm.example/v1", "m", Helpers.Secret);

        Helpers.Create(options).Provider.Should().Be(expected);
    }

    [Theory]
    [InlineData(AiProviderType.Anthropic, null, null, null, "AI_API_KEY is not set")]
    [InlineData(AiProviderType.Anthropic, null, null, "", "AI_API_KEY is not set")]
    [InlineData(AiProviderType.OpenAiCompatible, null, "m", Helpers.Secret, "An endpoint and model are required for an OpenAI-compatible provider")]
    [InlineData(AiProviderType.OpenAiCompatible, "https://llm.example/v1", null, Helpers.Secret, "An endpoint and model are required for an OpenAI-compatible provider")]
    [InlineData(AiProviderType.OpenAiCompatible, "not a url", "m", Helpers.Secret, "The AI endpoint is not a valid URL")]
    [InlineData(AiProviderType.Ollama, null, null, null, "A model is required for Ollama")]
    [InlineData(AiProviderType.Anthropic, "not a url", null, Helpers.Secret, "The AI endpoint is not a valid URL")]
    public async Task Incomplete_configuration_is_reported_as_misconfigured_without_the_key(AiProviderType type, string? url, string? model, string? key, string detail)
    {
        var port = Helpers.Create(new AiProviderOptions(type, url, model, key));

        var result = await port.Ask();

        result.IsT1.Should().BeTrue();
        result.AsT1.Reason.Should().Be(AiUnavailableReason.Misconfigured);
        result.AsT1.Detail.Should().Be(detail).And.NotContain(Helpers.Secret);
    }

    [Fact]
    public void Options_never_print_the_key()
    {
        var options = new AiProviderOptions(AiProviderType.Anthropic, "https://llm.example", "m", Helpers.Secret);

        options.ToString().Should().NotContain(Helpers.Secret).And.Contain("HasApiKey = True");
    }

    [Theory]
    [InlineData("none", AiProviderType.None)]
    [InlineData("OpenAI-Compatible", AiProviderType.OpenAiCompatible)]
    [InlineData("ollama", AiProviderType.Ollama)]
    [InlineData("anthropic", AiProviderType.Anthropic)]
    [InlineData("mock", AiProviderType.Mock)]
    public void ParseType_reads_the_settings_names(string value, AiProviderType expected) =>
        AiProviderOptions.ParseType(value).Should().Be(expected);

    [Fact]
    public void ParseType_rejects_unknown_names() =>
        AiProviderOptions.ParseType("gemini").Should().BeNull();
}
