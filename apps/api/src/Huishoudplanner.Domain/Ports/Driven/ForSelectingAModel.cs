using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Chooses the model to ask for the provider settings of one call. The settings can change at any time (and the connection test
/// asks about settings that are not stored yet), so the choice is made per call, never once at startup. The API key is not part of the
/// settings: the adapter reads it from the environment. Incomplete settings are not an error here: the returned port answers
/// <see cref="AiUnavailable"/>.
/// </summary>
public interface ForSelectingAModel
{
    ForChattingWithAModel ChooseFor(AiProviderSettings settings);
}
