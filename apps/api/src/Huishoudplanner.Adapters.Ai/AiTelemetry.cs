using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>
/// The adapter's own <see cref="ActivitySource"/> and <see cref="Meter"/> (plan section 3.6), named under the
/// <c>Huishoudplanner</c> root so the Host pipeline picks them up. Tags carry the provider and the outcome only,
/// never a key, an endpoint, a prompt or an answer.
/// </summary>
public static class AiTelemetry
{
    public const string Name = "Huishoudplanner.Ai";

    public const string CallsInstrument = "huishoudplanner.ai.calls";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>AI calls by provider and outcome (<c>ok</c>, <c>unavailable</c>, <c>timeout</c>, <c>http_error</c>, <c>unreachable</c>, <c>no_content</c>, <c>error</c>).</summary>
    internal static readonly Counter<long> Calls = Meter.CreateCounter<long>(
        CallsInstrument, unit: "{call}", description: "AI calls by provider and outcome");

    internal static void Record(string provider, string outcome) =>
        Calls.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("outcome", outcome));
}
