using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Adapters.Ai.Tests;

/// <summary>The per-call provider choice from the stored settings and the environment key (slice 6.1b).</summary>
public class AiModelSelectorTests
{
    [Theory]
    [InlineData(Domain.Settings.AiProviderType.None, "none")]
    [InlineData(Domain.Settings.AiProviderType.Mock, "mock")]
    [InlineData(Domain.Settings.AiProviderType.Anthropic, "anthropic")]
    [InlineData(Domain.Settings.AiProviderType.OpenAiCompatible, "openai-compatible")]
    [InlineData(Domain.Settings.AiProviderType.Ollama, "ollama")]
    public void The_provider_type_of_the_settings_selects_the_provider(Domain.Settings.AiProviderType type, string expected)
    {
        var selector = new AiModelSelector(() => Helpers.Secret);

        var port = selector.ChooseFor(new AiProviderSettings(type, "https://llm.example/v1", "m"));

        port.Provider.Should().Be(expected);
    }

    [Fact]
    public async Task A_missing_key_is_reported_as_misconfigured_and_a_key_makes_the_provider_usable()
    {
        var key = (string?)null;
        var selector = new AiModelSelector(() => key);
        var settings = new AiProviderSettings(Domain.Settings.AiProviderType.Anthropic);

        var withoutKey = await selector.ChooseFor(settings).Ask();
        key = Helpers.Secret;
        var withKey = selector.ChooseFor(settings);

        withoutKey.AsT1.Should().Be(new AiUnavailable(AiUnavailableReason.Misconfigured, "AI_API_KEY is not set"));
        withKey.Provider.Should().Be("anthropic");
    }

    [Fact]
    public async Task The_endpoint_and_model_of_the_settings_are_part_of_the_choice()
    {
        var selector = new AiModelSelector(() => null);

        var noEndpoint = await selector.ChooseFor(new AiProviderSettings(Domain.Settings.AiProviderType.OpenAiCompatible, null, "m")).Ask();
        var noModel = await selector.ChooseFor(new AiProviderSettings(Domain.Settings.AiProviderType.Ollama)).Ask();

        noEndpoint.AsT1.Detail.Should().Be("An endpoint and model are required for an OpenAI-compatible provider");
        noModel.AsT1.Detail.Should().Be("A model is required for Ollama");
    }

    [Fact]
    public async Task The_choice_is_made_on_every_call_so_a_settings_change_applies_at_once()
    {
        var selector = new AiModelSelector(() => null);

        var first = selector.ChooseFor(new AiProviderSettings(Domain.Settings.AiProviderType.None));
        var second = selector.ChooseFor(new AiProviderSettings(Domain.Settings.AiProviderType.Mock));

        first.Should().NotBeSameAs(second);
        (await first.Ask()).IsT1.Should().BeTrue();
        (await second.Ask()).IsT0.Should().BeTrue("the mock answers with its default responders");
    }

    [Fact]
    public async Task The_mock_uses_the_default_deterministic_responders()
    {
        var selector = new AiModelSelector(() => Helpers.Secret);
        var port = selector.ChooseFor(new AiProviderSettings(Domain.Settings.AiProviderType.Mock));

        var reply = await port.Ask(new ChatRequest(string.Empty, "x", new Domain.Ai.ChatOptions(Name: AiRequestNames.ConnectionTest)));

        reply.TextOf().Should().Be("{\"ok\":true}");
    }
}
