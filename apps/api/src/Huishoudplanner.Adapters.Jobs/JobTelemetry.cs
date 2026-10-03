using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Huishoudplanner.Adapters.Jobs;

/// <summary>
/// One span per job run and a counter of runs by job and outcome (plan section 3.6). The name sits under the application
/// root so the Host pipeline picks it up. Tags are the job name and the outcome only.
/// </summary>
public static class JobTelemetry
{
    public const string Name = "Huishoudplanner.Jobs";

    public const string RunsInstrument = "huishoudplanner.jobs.runs";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    private static readonly Counter<long> Runs = Meter.CreateCounter<long>(
        RunsInstrument, unit: "{run}", description: "Job runs by job and outcome.");

    internal static void Record(string job, JobOutcome outcome) =>
        Runs.Add(
            1,
            new KeyValuePair<string, object?>("job", job),
            new KeyValuePair<string, object?>("outcome", OutcomeTag(outcome)));

    internal static string OutcomeTag(JobOutcome outcome) => outcome switch
    {
        JobOutcome.Succeeded => "succeeded",
        JobOutcome.Skipped => "skipped",
        JobOutcome.Failed => "failed",
        JobOutcome.Overlapped => "overlapped",
        _ => "cancelled",
    };
}
