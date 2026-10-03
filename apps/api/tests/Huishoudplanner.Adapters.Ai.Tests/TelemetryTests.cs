using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Huishoudplanner.Domain.Ai;

namespace Huishoudplanner.Adapters.Ai.Tests;

public class TelemetryTests
{
    private sealed record Measurement(string Provider, string Outcome);

    private static List<Measurement> Capture(Action act)
    {
        var seen = new List<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AiTelemetry.Name && instrument.Name == AiTelemetry.CallsInstrument)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString() ?? string.Empty);
            lock (seen)
            {
                seen.Add(new Measurement(map["provider"], map["outcome"]));
            }
        });
        listener.Start();
        act();
        return seen;
    }

    [Fact]
    public async Task Calls_are_counted_by_provider_and_outcome_without_any_secret()
    {
        var measurements = new List<Measurement>();
        var spans = new List<Activity>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AiTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(activities);

        measurements.AddRange(Capture(() =>
        {
            Helpers.Create(new AiProviderOptions(AiProviderType.None)).Ask().GetAwaiter().GetResult();
            Helpers.Create(new AiProviderOptions(AiProviderType.Mock)).Ask(Helpers.Request with { Options = new ChatOptions(Name: "connection-test") }).GetAwaiter().GetResult();
            Helpers.Create(new AiProviderOptions(AiProviderType.OpenAiCompatible, "https://llm.example/v1", "m", Helpers.Secret), FakeHandler.Json(HttpStatusCode.Unauthorized, "{}")).Ask().GetAwaiter().GetResult();
            Helpers.Create(new AiProviderOptions(AiProviderType.Ollama, "http://o", "m"), FakeHandler.Throwing(new HttpRequestException(Helpers.Secret))).Ask().GetAwaiter().GetResult();
        }));

        measurements.Should().BeEquivalentTo(
        [
            new Measurement("none", "unavailable"),
            new Measurement("mock", "ok"),
            new Measurement("openai-compatible", "http_error"),
            new Measurement("ollama", "unreachable"),
        ]);
        spans.Should().NotBeEmpty();
        spans.SelectMany(s => s.TagObjects).Select(t => t.Value?.ToString()).Should().NotContain(v => v != null && v.Contains(Helpers.Secret, StringComparison.Ordinal));
    }
}
