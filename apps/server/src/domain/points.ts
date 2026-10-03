import {
  addDays,
  DEFAULT_CURRENCY_CODE,
  defaultPointsForDuration,
  expectedBonusEntries,
  fromDayKey,
  mondayOf,
  pointsToCents,
  toDayKey,
  MAX_POINTS_CORRECTIONS,
  type BonusOccurrence,
  type BonusScheduleRow,
  type ExpectedBonusEntry,
  type PointsBalancesQuery,
  type PointsBalancesResponse,
  type PointsBonusChange,
  type PointsCorrection,
  type PointEntryView,
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
  findBonusOccurrences,
  findDoneOccurrences,
  findOccurrenceById,
  setMissingPointsSnapshots,
  type BonusOccurrenceDoc,
  type OccurrenceDoc,
} from '../data/occurrences.ts';
import {
  applyBonusEntryChanges,
  applyPointEntryChanges,
  deletePointEntry,
  executionKey,
  findBonusPointEntries,
  findPointEntries,
  findPointEntriesInRange,
  findPointEntryByKey,
  insertPointEntry,
  POINTS_LEDGER_ID,
  sumPointEntries,
  updatePointEntry,
  type BonusEntryChanges,
  type PointEntryChanges,
  type PointEntryDoc,
  type PointEntryFields,
} from '../data/points.ts';
import { getSettings } from '../data/settings.ts';
import { defaultMissingTaskPoints, findTaskById, listTasks, type TaskDoc } from '../data/tasks.ts';
import { listUsers } from '../data/users.ts';
import { HttpError } from '../http/errors.ts';
import { toApi } from '../http/serialize.ts';
import { evaluateBadgeAwards } from './badgeAwards.ts';

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
 *
 * It runs in the same per-database queue as the reconciliation and the redemption bookings, so the
 * read-compare-write of one sync never interleaves with a reconciliation or a booking (ADR-0013). It reads
 * the occurrence inside the queue, so it always writes what is true at that moment. The reconciliation and
 * the booking never call it from inside the queue, which would wait for itself; keep it that way.
 */
export function syncExecutionPoints(
  ctx: AuditContext,
  occurrenceId: ObjectId,
  reason: PointsSyncReason,
): Promise<SyncOutcome> {
  return exclusively(ctx.db, () => syncNow(ctx, occurrenceId, reason));
}

async function syncNow(ctx: AuditContext, occurrenceId: ObjectId, reason: PointsSyncReason): Promise<SyncOutcome> {
  const settings = await getSettings(ctx.db);
  if (!settings) return 'unchanged';
  const occurrence = await findOccurrenceById(ctx.db, occurrenceId);
  const expected = occurrence ? expectedExecutionEntry(occurrence, settings.timezone) : null;
  const key = executionKey(occurrenceId);
  const stored = await findPointEntryByKey(ctx.db, key);
  const outcome = await syncLedgerEntry(ctx, key, expected, stored, { occurrenceId, reason });
  await syncBadgesAfter(ctx, occurrence, stored, reason);
  return outcome;
}

async function syncLedgerEntry(
  ctx: AuditContext,
  key: string,
  expected: PointEntryFields | null,
  stored: PointEntryDoc | null,
  meta: { occurrenceId: ObjectId; reason: PointsSyncReason },
): Promise<SyncOutcome> {
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

/**
 * Re-evaluates the badges of the person the change is about (ADR-0014): the person the execution is
 * credited to now and the one its ledger entry belonged to. An undo, a retract or a correction of work
 * that earned no ledger entry (a task of 0 points) does not say who held it before, so then everybody is
 * evaluated. A failure is logged and never fails the check-off that caused it; the nightly run repairs it.
 */
async function syncBadgesAfter(
  ctx: AuditContext,
  occurrence: OccurrenceDoc | null,
  stored: PointEntryDoc | null,
  reason: PointsSyncReason,
): Promise<void> {
  try {
    const people = new Map<string, ObjectId>();
    const credited = occurrence?.status === 'done' ? (occurrence.completedBy ?? occurrence.assigneeId) : null;
    for (const id of [stored?.personId, credited]) if (id) people.set(id.toHexString(), id);
    const everybody = !stored && reason !== 'complete' && reason !== 'recorded';
    if (!everybody && people.size === 0) return;
    await evaluateBadgeAwards(ctx, everybody ? null : [...people.values()], { mode: 'each', reason });
  } catch (err) {
    ctx.log.error({ err, occurrenceId: occurrence?._id.toHexString() }, 'badge evaluation failed');
  }
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

/** Maps an occurrence document to the shared bonus model; throws on a value that cannot be read (an invalid date, for example). */
function toBonusOccurrence(doc: BonusOccurrenceDoc, timezone: string): BonusOccurrence {
  if (doc.status !== 'open' && doc.status !== 'done' && doc.status !== 'skipped') throw new RangeError(`Unknown status: ${String(doc.status)}`);
  return {
    status: doc.status,
    plannedDate: toDayKey(doc.plannedDate, timezone),
    date: toDayKey(doc.date, timezone),
    recordedDone: doc.recordedDone === true,
    assigneeId: doc.assigneeId ? doc.assigneeId.toHexString() : null,
    // Missing means the assignee; a frozen null means it was unassigned.
    ...(doc.periodOwnerId === undefined ? {} : { periodOwnerId: doc.periodOwnerId ? doc.periodOwnerId.toHexString() : null }),
    completedBy: doc.completedBy ? doc.completedBy.toHexString() : null,
    completedAt: doc.completedAt ? doc.completedAt.toISOString() : null,
  };
}

/** The ledger fields of an expected bonus: dated on the last day of the period, at local midnight (ADR-0012). */
function bonusFields(entry: ExpectedBonusEntry, timezone: string): PointEntryFields {
  return {
    personId: new ObjectId(entry.personId),
    amount: entry.amount,
    date: fromDayKey(entry.periodEnd, timezone),
    weekStart: fromDayKey(mondayOf(entry.periodEnd), timezone),
    periodStart: fromDayKey(entry.periodStart, timezone),
    occurrenceId: null,
    taskId: null,
    titleSnapshot: '',
  };
}

function sameBonus(stored: PointEntryDoc, fields: PointEntryFields): boolean {
  return (
    stored.personId.equals(fields.personId) &&
    stored.amount === fields.amount &&
    stored.date.getTime() === fields.date.getTime() &&
    (stored.periodStart?.getTime() ?? null) === (fields.periodStart?.getTime() ?? null)
  );
}

/**
 * Step 4 of the reconciliation (ADR-0012): the week and cycle bonuses. They are a pure function of
 * the occurrences, the anchor, the timezone, today and the bonus schedule, and are only inserted or
 * deleted, never updated. An occurrence that cannot be read is counted in `skippedIds`; the bonus
 * entries of its owner are left as they are in this run, because their sets cannot be evaluated.
 */
async function reconcileBonuses(
  ctx: AuditContext,
  settings: { cycleAnchorDate: string; timezone: string; bonusSchedule?: BonusScheduleRow[] | undefined; bonusFloor?: string | undefined },
  storedBonuses: PointEntryDoc[],
  skippedIds: Set<string>,
  result: PointsRecomputeResult,
): Promise<void> {
  const timezone = settings.timezone;
  const items: BonusOccurrence[] = [];
  const blocked = new Set<string>();
  for (const doc of await findBonusOccurrences(ctx.db)) {
    try {
      items.push(toBonusOccurrence(doc, timezone));
    } catch (err) {
      ctx.log.warn({ err, occurrenceId: String(doc._id) }, 'bonus reconciliation skipped an unreadable occurrence');
      skippedIds.add(String(doc._id));
      // Every person the occurrence could count for: its owner and the person who did it.
      for (const person of [doc.assigneeId, doc.periodOwnerId, doc.completedBy]) if (person) blocked.add(String(person));
    }
  }

  const expected = new Map(
    expectedBonusEntries(items, {
      anchor: settings.cycleAnchorDate,
      timezone,
      today: toDayKey(ctx.clock.now(), timezone),
      schedule: settings.bonusSchedule ?? [],
      floor: settings.bonusFloor,
    })
      .filter((entry) => !blocked.has(entry.personId))
      .map((entry) => [entry.key, { entry, fields: bonusFields(entry, timezone) }] as const),
  );

  const changes: BonusEntryChanges = { inserts: [], deletes: [] };
  const storedByKey = new Map(storedBonuses.map((doc) => [doc.key, doc]));
  for (const [key, current] of storedByKey) {
    if (blocked.has(current.personId.toHexString())) continue;
    const want = expected.get(key);
    if (want && sameBonus(current, want.fields)) continue;
    changes.deletes.push(current);
  }
  for (const [key, { entry, fields }] of expected) {
    const current = storedByKey.get(key);
    if (current && sameBonus(current, fields)) continue;
    changes.inserts.push({ key, kind: entry.kind, fields });
  }

  const applied = await applyBonusEntryChanges(ctx, changes);
  // Only the writes that happened are counted and listed: a delete that missed its compare-and-set
  // and an insert that hit a duplicate key are not.
  const removedLog: PointsBonusChange[] = applied.removed.map((key) => {
    const doc = storedByKey.get(key)!;
    return { key, personId: doc.personId.toHexString(), amount: doc.amount, change: 'removed' };
  });
  const createdLog: PointsBonusChange[] = applied.created.map((key) => {
    const { entry } = expected.get(key)!;
    return { key, personId: entry.personId, amount: entry.amount, change: 'created' };
  });
  result.bonusesCreated = createdLog.length;
  result.bonusesRemoved = removedLog.length;
  const byKey = (a: PointsBonusChange, b: PointsBonusChange) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0);
  const log = [...removedLog.sort(byKey), ...createdLog.sort(byKey)];
  result.bonusChangesTotal = log.length;
  result.bonusChangesTruncated = log.length > MAX_POINTS_CORRECTIONS;
  result.bonusChanges = log.slice(0, MAX_POINTS_CORRECTIONS);
  if (applied.error !== undefined) throw applied.error;
}

/** Reconciliations never overlap in this process: a second run waits for the first one (ADR-0005: one process). */
const reconcileQueues = new Map<string, Promise<unknown>>();

/** Also serialises the redemption bookings, so the balance check and the insert of two bookings never interleave (ADR-0013). */
export function exclusively<T>(db: Db, run: () => Promise<T>): Promise<T> {
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
 * entry of every done occurrence and applies the differences in bulk. A fourth step inserts and
 * deletes the week and cycle bonuses of every ended period (ADR-0012). A fifth step makes the badge awards match (ADR-0014). A run that changes
 * something writes one summary audit entry (`points` / `recompute`) and nothing per entry; a run
 * that changes nothing writes and audits nothing. Entries of another kind than `execution` and the
 * four bonus kinds are never touched. Without settings there is nothing to reconcile.
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
    bonusesCreated: 0,
    bonusesRemoved: 0,
    bonusChanges: [],
    bonusChangesTotal: 0,
    bonusChangesTruncated: false,
  };
  const settings = await getSettings(ctx.db);
  if (!settings) return result;

  // Read the ledger before the occurrences: an entry written in between is then unknown to this
  // run and is left alone, instead of being deleted as an entry without an occurrence.
  const stored = new Map((await findPointEntries(ctx.db, { kind: 'execution' })).map((entry) => [entry.key, entry]));
  const storedBonuses = await findBonusPointEntries(ctx.db);

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
  // Occurrences that could not be read in any step, counted once each.
  const skippedIds = new Set<string>();
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
      skippedIds.add(occurrence._id.toHexString());
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

  // Step 4: the week and cycle bonuses (ADR-0012), inserted or deleted, never updated.
  // A failure here must not hide what steps 1 to 3 and the part of step 4 that ran have written, so
  // the summary is recorded first and the failure rethrown after it.
  let bonusFailure: { error: unknown } | null = null;
  try {
    await reconcileBonuses(ctx, settings, storedBonuses, skippedIds, result);
  } catch (error) {
    bonusFailure = { error };
  }
  result.skipped = skippedIds.size;

  if (
    result.tasksDefaulted + result.snapshotsSet + result.created + result.updated + result.removed + result.bonusesCreated + result.bonusesRemoved >
    0
  ) {
    await record(ctx, { entity: 'points', entityId: POINTS_LEDGER_ID, action: 'recompute', meta: { ...result } });
  }
  if (bonusFailure) throw bonusFailure.error;

  // Step 5: the badge awards (ADR-0014), derived from the executions and the on-time week bonuses that are now final.
  // They have their own summary entry, so the points summary and its result keep their shape.
  await evaluateBadgeAwards(ctx, null, { mode: 'summary', trigger });
  return result;
}

/** Balances per person for the range, `[from, to]` inclusive; all time without a range (ADR-0011). */
export async function pointsBalances(db: Db, query: PointsBalancesQuery): Promise<PointsBalancesResponse> {
  const settings = await getSettings(db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const centsPerPoint = settings.centsPerPoint ?? 0;
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
      const points = total?.points ?? 0;
      const redeemed = total?.redeemed ?? 0;
      const earned = points + redeemed;
      return {
        personId: user._id.toHexString(),
        points,
        earned,
        redeemed,
        // Money at the factor in force now, so earned minus redeemed is always the balance in money (ADR-0013).
        money:
          centsPerPoint > 0
            ? {
                earned: pointsToCents(earned, centsPerPoint),
                redeemed: pointsToCents(redeemed, centsPerPoint),
                balance: pointsToCents(points, centsPerPoint),
              }
            : null,
        executions: total?.executions ?? 0,
        bonusPoints: total?.bonusPoints ?? 0,
      };
    });
  return {
    from: query.from ?? null,
    to: query.to ?? null,
    currencyCode: settings.currencyCode ?? DEFAULT_CURRENCY_CODE,
    centsPerPoint,
    balances,
  };
}

/** The API view of a ledger entry: day keys instead of instants, and no request key (ADR-0002, ADR-0013). */
export function toPointEntryView(doc: PointEntryDoc, timezone: string): PointEntryView {
  const { requestId: _requestId, ...rest } = doc;
  return toApi({
    ...rest,
    date: toDayKey(doc.date, timezone),
    weekStart: toDayKey(doc.weekStart, timezone),
    periodStart: doc.periodStart ? toDayKey(doc.periodStart, timezone) : null,
    note: doc.note ?? null,
    centsPerPointSnapshot: doc.centsPerPointSnapshot ?? null,
    currencyCodeSnapshot: doc.currencyCodeSnapshot ?? null,
  }) as PointEntryView;
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
  return { entries: docs.map((doc) => toPointEntryView(doc, settings.timezone)) };
}
