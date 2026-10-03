using System.Globalization;
using System.Text.Json.Serialization;
using Huishoudplanner.Adapters.Http.CyclePlans;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Generation;

namespace Huishoudplanner.Adapters.Http.Jobs;

/// <summary>
/// What a manual generation run did: its id (the same id the audit entries of the run carry in <c>meta.runId</c>), how many open generated occurrences
/// it removed because they no longer matched the active plan, and what it generated for the current and the next cycle. The <c>due</c> summary of the
/// Node server (<c>{ due, overdue }</c>) joins this answer with the due list of slice 3.4.
/// </summary>
public sealed record GenerationRunResponse(string RunId, int Removed, IReadOnlyList<GenerationResultResponse> Generated)
{
    internal static GenerationRunResponse From(GenerationRun run) =>
        new(run.RunId, run.Removed, [.. run.Generated.Select(GenerationResultResponse.From)]);
}

/// <summary>
/// The answer of the manual audit retention job: <c>status</c> is <c>disabled</c> when <c>AUDIT_RETENTION_DAYS</c> is not set (then nothing else is
/// present) or <c>done</c> with the <c>cutoff</c> instant and the number of entries <c>deleted</c>.
/// </summary>
public sealed record AuditRetentionResponse(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cutoff = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Deleted = null)
{
    internal static AuditRetentionResponse Disabled() => new("disabled");

    internal static AuditRetentionResponse Done(RetentionDone done) =>
        new("done", done.Cutoff.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), done.Deleted);
}
