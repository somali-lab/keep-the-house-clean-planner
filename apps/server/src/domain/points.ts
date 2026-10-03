import {
  defaultPointsForDuration,
  fromDayKey,
  mondayOf,
  toDayKey,
  type PointsSyncReason,
} from '@huishoudplanner/shared';
import type { ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { findOccurrenceById, type OccurrenceDoc } from '../data/occurrences.ts';
import {
  deletePointEntry,
  executionKey,
  findPointEntryByKey,
  insertPointEntry,
  updatePointEntry,
  type PointEntryFields,
} from '../data/points.ts';
import { getSettings } from '../data/settings.ts';
import { findTaskById, type TaskDoc } from '../data/tasks.ts';

/** Points of a task; a task from before points existed earns the default for its duration. */
export function taskPoints(task: Pick<TaskDoc, 'points' | 'durationMinutes'>): number {
  return task.points ?? defaultPointsForDuration(task.durationMinutes);
}

/**
 * The value an occurrence snapshots when it becomes done (ADR-0011): the task's points, or the
 * duration rule for a one-off task and for a task that no longer exists. It never reads a task
 * value that was changed afterwards, because the snapshot is taken once, at completion.
 */
export async function pointsSnapshotFor(
  ctx: AuditContext,
  occurrence: Pick<OccurrenceDoc, 'taskId' | 'durationMinutesSnapshot'>,
): Promise<number> {
  const task = occurrence.taskId ? await findTaskById(ctx.db, occurrence.taskId) : null;
  return task ? taskPoints(task) : defaultPointsForDuration(occurrence.durationMinutesSnapshot);
}

/** The ledger fields an occurrence is expected to produce; null when it earns no entry (ADR-0011). */
export function expectedExecutionEntry(occurrence: OccurrenceDoc, timezone: string): PointEntryFields | null {
  if (occurrence.status !== 'done') return null;
  const personId = occurrence.completedBy ?? occurrence.assigneeId;
  const amount = occurrence.pointsSnapshot ?? 0;
  if (!personId || amount < 1) return null;
  return {
    personId,
    amount,
    date: occurrence.date,
    weekStart: fromDayKey(mondayOf(toDayKey(occurrence.date, timezone)), timezone),
    occurrenceId: occurrence._id,
    taskId: occurrence.taskId,
    titleSnapshot: occurrence.taskNameSnapshot,
  };
}

export type SyncOutcome = 'created' | 'updated' | 'deleted' | 'unchanged';

/**
 * Makes the single `execution:<occurrenceId>` ledger entry match its occurrence (ADR-0011): it
 * creates, updates or deletes that one entry through the data layer, with one audit entry per
 * real change and nothing on a no-op. The occurrence may be gone (retract, delete), in which
 * case any entry is removed. Run it after every successful write that can change an execution.
 */
export async function syncExecutionPoints(
  ctx: AuditContext,
  occurrenceId: ObjectId,
  reason: PointsSyncReason,
): Promise<SyncOutcome> {
  const settings = await getSettings(ctx.db);
  if (!settings) return 'unchanged';
  const occurrence = await findOccurrenceById(ctx.db, occurrenceId);
  const expected = occurrence ? expectedExecutionEntry(occurrence, settings.timezone) : null;
  const key = executionKey(occurrenceId);
  const meta = { occurrenceId, reason };

  const stored = await findPointEntryByKey(ctx.db, key);
  if (!expected) {
    return stored && (await deletePointEntry(ctx, stored, meta)) ? 'deleted' : 'unchanged';
  }
  if (!stored) {
    const result = await insertPointEntry(ctx, key, 'execution', expected, 'live', meta);
    if (result.inserted) return 'created';
    // A concurrent sync created it first: fall back to the update path.
    const winner = await findPointEntryByKey(ctx.db, key);
    if (!winner) return 'unchanged';
    return (await updatePointEntry(ctx, winner, expected, 'live', meta)) ? 'updated' : 'unchanged';
  }
  return (await updatePointEntry(ctx, stored, expected, 'live', meta)) ? 'updated' : 'unchanged';
}
