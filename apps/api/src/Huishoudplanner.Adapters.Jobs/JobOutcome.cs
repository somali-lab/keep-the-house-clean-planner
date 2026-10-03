namespace Huishoudplanner.Adapters.Jobs;

/// <summary>How one trigger of a job ended. The lower-case name is the <c>outcome</c> tag of the run counter.</summary>
public enum JobOutcome
{
    /// <summary>The job did its work (or found nothing to change).</summary>
    Succeeded,

    /// <summary>The job decided it had nothing to do, for example audit retention that is not configured.</summary>
    Skipped,

    /// <summary>The job failed; the failure is logged and never reaches the host.</summary>
    Failed,

    /// <summary>The previous run of the same job was still running, so this trigger did not start (noOverlap).</summary>
    Overlapped,

    /// <summary>The host was stopping.</summary>
    Cancelled,
}
