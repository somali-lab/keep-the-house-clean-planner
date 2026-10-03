using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Application.Occurrences;

/// <summary>
/// <c>lastCompletedAt</c> of a task is the newest <c>completedAt</c> of its done occurrences (<c>refreshLastCompletedAt</c> of the Node server). Shared
/// by the occurrence actions and the ad-hoc executions, which both move completions. A one-off task has no task record to refresh (ADR-0009) and a
/// task that is gone is skipped; the value is written, and audited with the occurrence that caused it, only when it changes. Runs inside the
/// caller's transaction.
/// </summary>
internal sealed class LastCompletedAtRefresh(ForStoringOccurrences occurrences, ForStoringTasks tasks, ForRecordingAudit audit)
{
    public async Task<Step<bool>> RunAsync(AuditActor actor, string? taskId, string occurrenceId, DateTimeOffset now, CancellationToken ct)
    {
        if (taskId is null)
        {
            return false;
        }

        if (!(await occurrences.FindLatestCompletionAsync(taskId, ct).ConfigureAwait(false)).AsStep().TryGet(out var latest, out var latestFailure))
        {
            return latestFailure;
        }

        if (!(await tasks.FindAsync(taskId, ct).ConfigureAwait(false)).AsOptional().TryGet(out var task, out var taskFailure))
        {
            return taskFailure;
        }

        if (task is null || task.LastCompletedAt == latest.At)
        {
            return false;
        }

        var written = await tasks.SetLastCompletedAtAsync(taskId, latest.At, now, ct).ConfigureAwait(false);
        if (!written.AsStep().TryGet(out _, out var writeFailure))
        {
            return writeFailure;
        }

        var entry = TaskAudit.ForLastCompletedAt(actor, taskId, task.LastCompletedAt, latest.At, occurrenceId);
        if (entry is null)
        {
            return false;
        }

        var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
        return recorded.Match<Step<bool>>(_ => true, error => error);
    }
}
