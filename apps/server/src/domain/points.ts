import {
  addDays,
  defaultPointsForDuration,
  fromDayKey,
  mondayOf,
  toDayKey,
  MAX_POINTS_CORRECTIONS,
  type PointsBalancesQuery,
  type PointsBalancesResponse,
  type PointsCorrection,
  type PointsEntriesQuery,
  type PointsEntriesResponse,
  type PointsRecomputeResult,
  type PointsRecomputeTrigger,
  type PointsSyncReason,
} from '@huishoudplanner/shared';
import { ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { record } from '../audit/record.ts';
import {
  findDoneOccurrences,
  findOccurrenceById,
  setMissingPointsSnapshots,
  type OccurrenceDoc,
} from '../data/occurrences.ts';
import {
  applyPointEntryChanges,
  deletePointEntry,
  executionKey,
  findPointEntries,
  findPointEntriesInRange,
  findPointEntryByKey,
  insertPointEntry,
  POINTS_LEDGER_ID,
  sumPointEntries,
  updatePointEntry,
  type PointEntryChanges,
  type PointEntryFields,
} from '../data/points.ts';
import { getSettings } from '../data/settings.ts';
import { defaultMissingTaskPoints, findTaskById, listTasks, type TaskDoc } from '../data/tasks.ts';
import { listUsers } from '../data/users.ts';
import { HttpError } from '../http/errors.ts';
import { toApi } from '../http/serialize.ts';

/** Points of a task; a task from before points existed earns the default for its duration. */
export function taskPoints(task: Pick<TaskDoc, 'points' | 'durationMinutes'>): number {
  return task.points ?? defaultPointsForDuration(task.durationMinutes);
}

/**
 * The value an occurrence snapshots when it becomes done (ADR-0011): the points a one-off task was
 * recorded with, else the task's points, or the duration rule for a one-off task and for a task that no longer exists. It never reads a task
 * value that was changed afterwards, because the snapshot is taken once, at completion.
 */
export async function pointsSnapshotFor(
  ctx: AuditContext,
  occurrence: Pick<OccurrenceDoc, 'taskId' | 'durationMinutesSnapshot' | 'pointsOverride'>,
): Promise<number> {
  if (occurrence.pointsOverride != null) return occurrence.pointsOverride;
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

function sameFields(a: PointEntryFields, b: PointEntryFields): boolean {
  return (
    a.personId.equals(b.personId) &&
    a.amount === b.amount &&
    a.date.getTime() === b.date.getTime() &&
    a.weekStart.getTime() === b.weekStart.getTime() &&
    (a.occurrenceId === null ? b.occurrenceId === null : b.occurrenceId !== null && a.occurrenceId.equals(b.occurrenceId)) &&
    (a.taskId === null ? b.taskId === null : b.taskId !== null && a.taskId.equals(b.taskId)) &&
    a.titleSnapshot === b.titleSnapshot
  );
}

/** Reconciliations never overlap in this process: a second run waits for the first one (ADR-0005: one process). */
const reconcileQueues = new Map<string, Promise<unknown>>();

function exclusively<T>(db: Db, run: () => Promise<T>): Promise<T> {
  const queue = reconcileQueues.get(db.databaseName) ?? Promise.resolve();
  const next = queue.then(run, run);
  const tail = next.catch(() => undefined);
  reconcileQueues.set(db.databaseName, tail);
  void tail.then(() => {
    if (reconcileQueues.get(db.databaseName) === tail) reconcileQueues.delete(db.databaseName);
  });
  return next;
}

/**
 * Makes the whole ledger match the occurrences (ADR-0011), idempotently. It migrates the fields
 * (tasks without points, done occurrences without a snapshot), computes the expected execution
 * entry of every done occurrence and applies the differences in bulk. A run that changes
 * something writes one summary audit entry (`points` / `recompute`) and nothing per entry; a run
 * that changes nothing writes and audits nothing. Entries that are not of kind `execution` are
 * never touched. Without settings there is nothing to reconcile.
 *
 * Runs never overlap, the stored entries are read before the occurrences, and every change is a
 * compare-and-set on the entry that was read, so a check-off that lands meanwhile is never undone.
 * An occurrence that cannot be read (an invalid date) is skipped and counted, never fatal.
 */
export function reconcilePoints(ctx: AuditContext, trigger: PointsRecomputeTrigger): Promise<PointsRecomputeResult> {
  return exclusively(ctx.db, () => reconcileNow(ctx, trigger));
}

/**
 * Runs the reconciliation for a caller that must not fail because of it (startup, the nightly
 * job, an import): a failure is logged and answered with null.
 */
export async function reconcilePointsSafely(
  ctx: AuditContext,
  trigger: PointsRecomputeTrigger,
  reconcile: typeof reconcilePoints = reconcilePoints,
): Promise<PointsRecomputeResult | null> {
  try {
    return await reconcile(ctx, trigger);
  } catch (err) {
    ctx.log.error({ err, trigger }, 'points reconciliation failed');
    return null;
  }
}

async function reconcileNow(ctx: AuditContext, trigger: PointsRecomputeTrigger): Promise<PointsRecomputeResult> {
  const result: PointsRecomputeResult = {
    trigger,
    tasksDefaulted: 0,
    snapshotsSet: 0,
    created: 0,
    updated: 0,
    removed: 0,
    unattributed: 0,
    skipped: 0,
    corrections: [],
    correctionsTotal: 0,
    correctionsTruncated: false,
  };
  const settings = await getSettings(ctx.db);
  if (!settings) return result;

  // Read the ledger before the occurrences: an entry written in between is then unknown to this
  // run and is left alone, instead of being deleted as an entry without an occurrence.
  const stored = new Map((await findPointEntries(ctx.db, { kind: 'execution' })).map((entry) => [entry.key, entry]));

  // Step 1: migrate the fields. Both writes filter on the missing field, so a second run matches nothing.
  const defaulted = await defaultMissingTaskPoints(ctx.db);
  result.tasksDefaulted = defaulted.count;
  const migrated = new Set(defaulted.ids.map((id) => id.toHexString()));
  const done = await findDoneOccurrences(ctx.db);
  const unsnapshotted = done.filter((doc) => doc.pointsSnapshot == null);
  if (unsnapshotted.length > 0) {
    const tasks = new Map((await listTasks(ctx.db)).map((task) => [task._id.toHexString(), task]));
    const snapshots = unsnapshotted.map((doc) => {
      if (doc.pointsOverride != null) return { id: doc._id, points: doc.pointsOverride };
      const taskId = doc.taskId?.toHexString();
      const task = taskId ? tasks.get(taskId) : undefined;
      // A task that no longer exists, a one-off task and a task whose points this migration just
      // filled in (it never had a value of its own) take the duration rule from the occurrence snapshot.
      const useTask = task && taskId && !migrated.has(taskId);
      return { id: doc._id, points: useTask ? taskPoints(task) : defaultPointsForDuration(doc.durationMinutesSnapshot) };
    });
    result.snapshotsSet = await setMissingPointsSnapshots(ctx.db, snapshots);
    // Continue with the values that were just written, without reading every done occurrence again.
    const written = new Map(snapshots.map(({ id, points }) => [id.toHexString(), points]));
    for (const doc of unsnapshotted) doc.pointsSnapshot = written.get(doc._id.toHexString()) ?? null;
  }

  // Step 2: the expected entry of every done occurrence.
  const expected = new Map<string, PointEntryFields>();
  const unreadable = new Set<string>();
  for (const occurrence of done) {
    const key = executionKey(occurrence._id);
    try {
      const fields = expectedExecutionEntry(occurrence, settings.timezone);
      if (fields) expected.set(key, fields);
      else if (!(occurrence.completedBy ?? occurrence.assigneeId) && (occurrence.pointsSnapshot ?? 0) >= 1) result.unattributed += 1;
    } catch (err) {
      // Old data can hold anything; one bad row must not stop the others. Its stored entry stays as it is.
      ctx.log.warn({ err, occurrenceId: key }, 'points reconciliation skipped an unreadable occurrence');
      unreadable.add(key);
      result.skipped += 1;
    }
  }

  // Step 3: the differences.
  const changes: PointEntryChanges = { inserts: [], updates: [], deletes: [] };
  const corrections: PointsCorrection[] = [];
  for (const [key, fields] of expected) {
    const current = stored.get(key);
    if (!current) {
      changes.inserts.push({ key, kind: 'execution', fields });
    } else if (!sameFields(current, fields)) {
      changes.updates.push({ current, fields });
      corrections.push({
        key,
        from: { personId: current.personId.toHexString(), amount: current.amount },
        to: { personId: fields.personId.toHexString(), amount: fields.amount },
      });
    }
  }
  for (const [key, current] of stored) {
    if (expected.has(key) || unreadable.has(key)) continue;
    changes.deletes.push(current);
    corrections.push({ key, from: { personId: current.personId.toHexString(), amount: current.amount }, to: null });
  }
  const applied = await applyPointEntryChanges(ctx, changes);
  result.created = applied.created;
  result.updated = applied.updated;
  result.removed = applied.removed;
  result.correctionsTotal = corrections.length;
  result.correctionsTruncated = corrections.length > MAX_POINTS_CORRECTIONS;
  result.corrections = corrections.slice(0, MAX_POINTS_CORRECTIONS);

  if (result.tasksDefaulted + result.snapshotsSet + result.created + result.updated + result.removed > 0) {
    await record(ctx, { entity: 'points', entityId: POINTS_LEDGER_ID, action: 'recompute', meta: { ...result } });
  }
  return result;
}

/** Balances per person for the range, `[from, to]` inclusive; all time without a range (ADR-0011). */
export async function pointsBalances(db: Db, query: PointsBalancesQuery): Promise<PointsBalancesResponse> {
  const settings = await getSettings(db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const totals = new Map(
    (
      await sumPointEntries(db, {
        ...(query.from ? { from: fromDayKey(query.from, settings.timezone) } : {}),
        ...(query.to ? { to: fromDayKey(addDays(query.to, 1), settings.timezone) } : {}),
      })
    ).map((total) => [total.personId.toHexString(), total]),
  );
  // Every active user, also at 0, and every inactive user with entries; in the order of the user list.
  const balances = (await listUsers(db))
    .filter((user) => user.active || totals.has(user._id.toHexString()))
    .map((user) => {
      const total = totals.get(user._id.toHexString());
      return { personId: user._id.toHexString(), points: total?.points ?? 0, executions: total?.executions ?? 0 };
    });
  return { from: query.from ?? null, to: query.to ?? null, balances };
}

/** One person's ledger entries in `[from, to]`, newest date first, then by id. */
export async function pointEntriesOfPerson(db: Db, query: PointsEntriesQuery): Promise<PointsEntriesResponse> {
  const settings = await getSettings(db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const docs = await findPointEntriesInRange(
    db,
    new ObjectId(query.personId),
    fromDayKey(query.from, settings.timezone),
    fromDayKey(addDays(query.to, 1), settings.timezone),
  );
  return {
    entries: docs.map((doc) =>
      toApi({
        ...doc,
        date: toDayKey(doc.date, settings.timezone),
        weekStart: toDayKey(doc.weekStart, settings.timezone),
      }),
    ) as PointsEntriesResponse['entries'],
  };
}
