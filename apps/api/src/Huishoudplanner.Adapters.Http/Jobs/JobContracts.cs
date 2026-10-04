using System.Globalization;
using System.Text.Json.Serialization;
using Huishoudplanner.Adapters.Http.CyclePlans;
using Huishoudplanner.Adapters.Http.Due;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Notifications;

namespace Huishoudplanner.Adapters.Http.Jobs;

/// <summary>
/// What a manual generation run did: its id (the same id the audit entries of the run carry in <c>meta.runId</c>), how many open generated occurrences
/// it removed because they no longer matched the active plan, what it generated for the current and the next cycle, and the <c>due</c> summary
/// (<c>{ due, overdue }</c>) of the due list after the run.
/// </summary>
public sealed record GenerationRunResponse(string RunId, int Removed, IReadOnlyList<GenerationResultResponse> Generated, DueSummaryResponse Due)
{
    internal static GenerationRunResponse From(GenerationRun run, DueSummary due) =>
        new(run.RunId, run.Removed, [.. run.Generated.Select(GenerationResultResponse.From)], new DueSummaryResponse(due.Due, due.Overdue));
}

/// <summary>
/// What a morning notification run did: <c>status</c> is <c>disabled</c> (no notification channel, nothing sent), <c>done</c> or <c>error</c> (the data could not
/// be read, nothing sent); <c>date</c> is the household day (null unless done), <c>sent</c> and <c>failed</c> count the deliveries and <c>quiet</c> the people with
/// nothing to report.
/// </summary>
public sealed record MorningNotifyResponse(string Status, DateOnly? Date, int Sent, int Failed, int Quiet)
{
    internal static MorningNotifyResponse From(MorningResult result) => new(
        result.Status switch
        {
            MorningStatus.Disabled => "disabled",
            MorningStatus.Error => "error",
            _ => "done",
        },
        result.Date,
        result.Sent,
        result.Failed,
        result.Quiet);
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
