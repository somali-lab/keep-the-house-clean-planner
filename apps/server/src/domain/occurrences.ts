import {
  cycleIndexFor,
  defaultPointsForDuration,
  fromDayKey,
  today,
  toDayKey,
  weekdaySun0,
  type ApiWarning,
  type OccurrenceView,
} from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { findCycleByIndex } from '../data/cycles.ts';
import {
  deleteOccurrences,
  findOccurrenceById,
  findOccurrenceByRequestId,
  findOccurrences,
  insertAdhocOccurrence,
  retractRecordedOccurrence,
  updateOccurrence,
  type OccurrenceDoc,
} from '../data/occurrences.ts';
import { getSettings } from '../data/settings.ts';
import { findTaskById, setTaskLastCompletedAt } from '../data/tasks.ts';
import { findRoomById } from '../data/rooms.ts';
import { findUserById } from '../data/users.ts';
import { HttpError, notFound } from '../http/errors.ts';
import { toApi } from '../http/serialize.ts';
import { pointsSnapshotFor, syncExecutionPoints, taskPoints } from './points.ts';

/** API view: calendar dates as day keys, plus derived overdue and drag info. */
export function toOccurrenceView(doc: OccurrenceDoc, todayKey: string, timezone: string): OccurrenceView {
  const date = toDayKey(doc.date, timezone);
  const plannedDate = toDayKey(doc.plannedDate, timezone);
  return {
    ...toApi(doc),
    date,
    plannedDate,
    recordedDone: doc.recordedDone ?? false,
    requestId: doc.requestId ?? null,
    pointsSnapshot: doc.pointsSnapshot ?? null,
    isOverdue: doc.status === 'open' && date < todayKey,
    movedFrom: date === plannedDate ? null : plannedDate,
  };
}

async function requireOccurrence(ctx: AuditContext, id: ObjectId): Promise<OccurrenceDoc> {
  const occ = await findOccurrenceById(ctx.db, id);
  if (!occ) throw notFound('occurrence');
  return occ;
}

const invalidTransition = (from: string, action: string) =>
  new HttpError(409, 'invalid_transition', `Cannot ${action} an occurrence that is ${from}`, { status: from, action });

/** lastCompletedAt = newest completedAt of the task's done occurrences. */
async function refreshLastCompletedAt(ctx: AuditContext, taskId: ObjectId | null, occurrenceId: ObjectId): Promise<void> {
  if (!taskId) return; // a one-off task has no task record to refresh (ADR-0009)
  const done = await findOccurrences(ctx.db, { taskId, status: 'done', completedAt: { $ne: null } });
  const latest = done.reduce<Date | null>(
    (max, o) => (o.completedAt && (!max || o.completedAt > max) ? o.completedAt : max),
    null,
  );
  await setTaskLastCompletedAt(ctx, taskId, latest, { occurrenceId });
}

export async function completeOccurrence(
  ctx: AuditContext,
  id: ObjectId,
  completedByInput?: ObjectId,
  takeOver = false,
): Promise<OccurrenceDoc> {
  const current = await requireOccurrence(ctx, id);
  if (current.status === 'done') throw invalidTransition('done', 'complete');

  // completedBy is the person credited (ADR-0011). Work of someone else never defaults to the
  // assignee: the request must say who performed it, either a named person or a take over.
  // Unassigned work and work of the actor itself default to the actor.
  const assignedToSomeoneElse = current.assigneeId !== null && !current.assigneeId.equals(ctx.actorId);
  if (assignedToSomeoneElse && !takeOver && !completedByInput) {
    throw new HttpError(400, 'validation_error', 'Choose who performed this task', [
      { field: 'completedBy', message: 'completion_choice_required' },
    ]);
  }
  const completedBy = takeOver ? ctx.actorId : (completedByInput ?? ctx.actorId);
  if (completedByInput) {
    const user = await findUserById(ctx.db, completedByInput);
    if (!user?.active) {
      throw new HttpError(400, 'validation_error', 'Invalid completedBy', [
        { field: 'completedBy', message: user ? 'inactive_user' : 'unknown_user' },
      ]);
    }
  }

  const wasAssignee = current.assigneeId?.equals(completedBy) ?? false;
  const claimed = current.assigneeId === null;
  const reassigned = takeOver && !(current.assigneeId?.equals(ctx.actorId) ?? false);
  const result = await updateOccurrence(
    ctx,
    id,
    {
      status: 'done',
      statusBeforeCompletion: current.status,
      completedAt: ctx.clock.now(),
      completedBy,
      pointsSnapshot: await pointsSnapshotFor(ctx, current),
      // Completing unassigned work claims it; taking over assigned work transfers it to the actor.
      ...(claimed || takeOver ? { assigneeId: completedBy } : {}),
    },
    {
      action: 'complete',
      meta: {
        completedBy,
        wasAssignee,
        ...(claimed ? { claimed: true } : {}),
        ...(reassigned ? { takenOver: true, previousAssigneeId: current.assigneeId } : {}),
      },
    },
    { status: current.status },
  );
  if (!result) throw invalidTransition('changed', 'complete');
  await refreshLastCompletedAt(ctx, current.taskId, id);
  await syncExecutionPoints(ctx, id, 'complete');
  return result.after;
}

export async function uncompleteOccurrence(ctx: AuditContext, id: ObjectId): Promise<OccurrenceDoc> {
  const current = await requireOccurrence(ctx, id);
  if (current.status !== 'done') throw invalidTransition(current.status, 'uncomplete');
  // Recorded work has no planned state to return to; reopening it would leave a record that later counts as missed.
  if (current.recordedDone) {
    throw new HttpError(409, 'retract_required', 'Recorded work cannot be uncompleted; retract it instead');
  }
  const result = await updateOccurrence(
    ctx,
    id,
    {
      status: current.statusBeforeCompletion ?? 'open',
      statusBeforeCompletion: null,
      completedAt: null,
      completedBy: null,
      // The snapshot belongs to one completion; a later check-off takes the value of that moment.
      pointsSnapshot: null,
    },
    { action: 'uncomplete' },
    { status: 'done' },
  );
  if (!result) throw invalidTransition('changed', 'uncomplete');
  await refreshLastCompletedAt(ctx, current.taskId, id);
  await syncExecutionPoints(ctx, id, 'uncomplete');
  return result.after;
}

export async function editCompletion(
  ctx: AuditContext,
  id: ObjectId,
  input: { date: string; completedAt: string; completedBy: ObjectId },
): Promise<OccurrenceDoc> {
  const settings = await getSettings(ctx.db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const current = await requireOccurrence(ctx, id);
  if (current.status !== 'done') throw invalidTransition(current.status, 'edit completion of');

  const user = await findUserById(ctx.db, input.completedBy);
  if (!user) {
    throw new HttpError(400, 'validation_error', 'Invalid completedBy', [
      { field: 'completedBy', message: 'unknown_user' },
    ]);
  }

  const currentDate = toDayKey(current.date, settings.timezone);
  const cycle = input.date === currentDate
    ? null
    : await findCycleByIndex(ctx.db, cycleIndexFor(input.date, settings.cycleAnchorDate));
  if (input.date !== currentDate && !cycle) {
    throw new HttpError(409, 'cycle_not_generated', 'That day is not generated yet', undefined, { date: input.date });
  }

  const result = await updateOccurrence(
    ctx,
    id,
    {
      date: fromDayKey(input.date, settings.timezone),
      completedAt: new Date(input.completedAt),
      completedBy: input.completedBy,
      ...(cycle && !cycle._id.equals(current.cycleId) ? { cycleId: cycle._id } : {}),
    },
    { action: 'update', meta: { correction: 'completion' } },
    { status: 'done' },
  );
  if (!result) throw invalidTransition('changed', 'edit completion of');
  await refreshLastCompletedAt(ctx, current.taskId, id);
  await syncExecutionPoints(ctx, id, 'correction');
  return result.after;
}

export async function deleteCompletedOccurrence(ctx: AuditContext, id: ObjectId): Promise<void> {
  const current = await requireOccurrence(ctx, id);
  if (current.status !== 'done') throw invalidTransition(current.status, 'delete');
  await deleteOccurrences(ctx, [current], { correction: 'completion' });
  await refreshLastCompletedAt(ctx, current.taskId, id);
  await syncExecutionPoints(ctx, id, 'correction');
}

/**
 * Undo of recorded work: an audited delete (reason retract) that also restores lastCompletedAt.
 * It is an undo, not a correction: only work of today can be retracted by any profile. Deleting
 * older completions stays an administrator's correction (DELETE /occurrences/:id).
 */
export async function retractOccurrence(ctx: AuditContext, id: ObjectId): Promise<void> {
  const current = await requireOccurrence(ctx, id);
  if (current.origin !== 'adhoc' || !current.recordedDone || current.status !== 'done') {
    throw new HttpError(409, 'not_retractable', 'Only recorded extra work can be retracted');
  }
  const settings = await getSettings(ctx.db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  if (toDayKey(current.date, settings.timezone) !== today(settings.timezone, ctx.clock.now())) {
    throw new HttpError(409, 'retract_not_today', 'Only work recorded today can be retracted');
  }
  // A concurrent retract already removed it: the same answer as a second retract.
  const deleted = await retractRecordedOccurrence(ctx, id);
  if (!deleted) throw notFound('occurrence');
  await refreshLastCompletedAt(ctx, current.taskId, id);
  await syncExecutionPoints(ctx, id, 'retract');
}

export async function skipOccurrence(ctx: AuditContext, id: ObjectId, reason?: string): Promise<OccurrenceDoc> {
  const current = await requireOccurrence(ctx, id);
  if (current.status !== 'open') throw invalidTransition(current.status, 'skip');
  const result = await updateOccurrence(
    ctx,
    id,
    { status: 'skipped', skipReason: reason ? reason : null },
    { action: 'skip' },
    { status: 'open' },
  );
  if (!result) throw invalidTransition('changed', 'skip');
  return result.after;
}

export interface ChangeResult {
  doc: OccurrenceDoc;
  warnings: ApiWarning[];
}

/** Moving or assigning onto a day the assignee is unavailable is allowed, but reported. */
async function unavailableWarnings(ctx: AuditContext, assigneeId: ObjectId | null, dayKey: string): Promise<ApiWarning[]> {
  if (!assigneeId) return [];
  const user = await findUserById(ctx.db, assigneeId);
  const weekday = weekdaySun0(dayKey);
  if (!user?.unavailableWeekdays.includes(weekday)) return [];
  return [
    {
      code: 'assignee_unavailable',
      message: 'Assignee is not available on this weekday',
      details: { userId: assigneeId.toHexString(), weekday },
    },
  ];
}

/**
 * Drag to another day: `plannedDate` stays so drift is measurable; `cycleId`
 * follows the new date. Only open occurrences, only onto generated days.
 */
export async function rescheduleOccurrence(ctx: AuditContext, id: ObjectId, date: string): Promise<ChangeResult> {
  const settings = await getSettings(ctx.db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const current = await requireOccurrence(ctx, id);
  if (current.status !== 'open') throw invalidTransition(current.status, 'reschedule');

  const from = toDayKey(current.date, settings.timezone);
  if (from === date) return { doc: current, warnings: [] };

  const cycle = await findCycleByIndex(ctx.db, cycleIndexFor(date, settings.cycleAnchorDate));
  if (!cycle) {
    throw new HttpError(409, 'cycle_not_generated', 'That day is not generated yet', undefined, { date });
  }

  const result = await updateOccurrence(
    ctx,
    id,
    {
      date: fromDayKey(date, settings.timezone),
      ...(cycle._id.equals(current.cycleId) ? {} : { cycleId: cycle._id }),
    },
    { action: 'reschedule', meta: { from, to: date } },
    { status: 'open' },
  );
  if (!result) throw invalidTransition('changed', 'reschedule');
  return { doc: result.after, warnings: await unavailableWarnings(ctx, result.after.assigneeId, date) };
}

export async function assignOccurrence(ctx: AuditContext, id: ObjectId, assigneeId: ObjectId | null): Promise<ChangeResult> {
  const settings = await getSettings(ctx.db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const current = await requireOccurrence(ctx, id);
  if (current.status !== 'open') throw invalidTransition(current.status, 'assign');
  if (assigneeId) {
    const user = await findUserById(ctx.db, assigneeId);
    if (!user?.active) {
      throw new HttpError(400, 'validation_error', 'Invalid assignee', [
        { field: 'assigneeId', message: user ? 'inactive_user' : 'unknown_user' },
      ]);
    }
  }

  const result = await updateOccurrence(ctx, id, { assigneeId }, { action: 'assign' }, { status: 'open' });
  if (!result) throw invalidTransition('changed', 'assign');
  const changed = result.after !== result.before;
  return {
    doc: result.after,
    warnings: changed ? await unavailableWarnings(ctx, assigneeId, toDayKey(result.after.date, settings.timezone)) : [],
  };
}

export interface AdhocOccurrenceInput {
  taskId: ObjectId;
  date: string;
  /** undefined = the task's default assignee (the actor when done); null = "wie dan ook". */
  assigneeId?: ObjectId | null;
  /** Record the work as already done; only allowed for today. */
  done?: boolean;
  /** Client idempotency key; a repeat of the same request replays the stored record. */
  requestId?: string;
}

/** A one-off task (ADR-0009): no task record, everything lives in the occurrence snapshots. */
export interface OneOffOccurrenceInput {
  name: string;
  roomId?: ObjectId | null;
  durationMinutes: number;
  date: string;
  /** undefined = unassigned (the actor when done); null = "wie dan ook". */
  assigneeId?: ObjectId | null;
  done?: boolean;
  requestId?: string;
}

export interface AdhocResult extends ChangeResult {
  /** False when a repeated requestId replayed an existing record. */
  created: boolean;
}

const idempotencyConflict = () =>
  new HttpError(409, 'idempotency_key_conflict', 'This request key was already used for a different request');

/** What a repeated request key must match to count as the same request. */
interface RequestIdentity {
  /** Existing task, or null for a one-off task (matched on its snapshot name). */
  taskId: ObjectId | null;
  name: string;
  date: string;
  done: boolean;
}

/** The same key for the same request returns the stored record; anything else is a conflict. */
function replayOrConflict(existing: OccurrenceDoc, identity: RequestIdentity, timezone: string): AdhocResult {
  const sameTarget = identity.taskId
    ? (existing.taskId?.equals(identity.taskId) ?? false)
    : existing.taskId === null && existing.taskNameSnapshot === identity.name;
  const same =
    sameTarget &&
    toDayKey(existing.plannedDate, timezone) === identity.date &&
    (existing.recordedDone ?? false) === identity.done;
  if (!same) throw idempotencyConflict();
  return { doc: existing, warnings: [], created: false };
}

type Settings = NonNullable<Awaited<ReturnType<typeof getSettings>>>;

async function requireSettings(ctx: AuditContext): Promise<Settings> {
  const settings = await getSettings(ctx.db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  return settings;
}

/** Recorded work must be for today and by a person; the actor is the default person. */
function assertRecordedWorkRules(
  ctx: AuditContext,
  settings: Settings,
  input: { date: string; done: boolean; assigneeId?: ObjectId | null },
): void {
  if (!input.done) return;
  if (input.date !== today(settings.timezone, ctx.clock.now())) {
    throw new HttpError(400, 'validation_error', 'Recorded work must be for today', [
      { field: 'date', message: 'done_requires_today' },
    ]);
  }
  if (input.assigneeId === null) {
    throw new HttpError(400, 'validation_error', 'Someone did the work', [
      { field: 'assigneeId', message: 'done_requires_person' },
    ]);
  }
}

async function assertActiveAssignee(ctx: AuditContext, assigneeId: ObjectId | null): Promise<void> {
  if (!assigneeId) return;
  const user = await findUserById(ctx.db, assigneeId);
  if (!user?.active) {
    throw new HttpError(400, 'validation_error', 'Invalid assignee', [
      { field: 'assigneeId', message: user ? 'inactive_user' : 'unknown_user' },
    ]);
  }
}

async function requireGeneratedCycle(ctx: AuditContext, settings: Settings, date: string) {
  const cycle = await findCycleByIndex(ctx.db, cycleIndexFor(date, settings.cycleAnchorDate));
  if (!cycle) {
    throw new HttpError(409, 'cycle_not_generated', 'That day is not generated yet', undefined, { date });
  }
  return cycle;
}

/** Inserts the record; losing a race against the same key lets the winner decide between replay and conflict. */
async function insertAdhoc(
  ctx: AuditContext,
  settings: Settings,
  doc: OccurrenceDoc,
  kind: 'extra' | 'one_off',
  identity: RequestIdentity,
  warnings: ApiWarning[],
): Promise<AdhocResult> {
  const result = await insertAdhocOccurrence(ctx, doc, {
    origin: 'adhoc',
    kind,
    recordedDone: doc.recordedDone ?? false,
    requestId: doc.requestId ?? null,
  });
  if (!result.inserted) {
    const existing = doc.requestId ? await findOccurrenceByRequestId(ctx.db, doc.requestId) : null;
    if (!existing) throw idempotencyConflict();
    return replayOrConflict(existing, identity, settings.timezone);
  }
  return { doc: result.doc, warnings, created: true };
}

/**
 * Keeps the ledger in line with a created or replayed ad-hoc record (ADR-0011). A replay normally
 * finds the entry in place and writes nothing; it only repairs one that a lost write left behind.
 */
async function syncAdhocPoints(ctx: AuditContext, result: AdhocResult): Promise<AdhocResult> {
  if (result.doc.status === 'done') await syncExecutionPoints(ctx, result.doc._id, 'recorded');
  return result;
}

/**
 * An extra execution of an existing task (ADR-0009): planned on a day outside the template
 * or, with `done`, recorded as already done today. Only within cycles that are already
 * generated, so exports and generation never see a half-filled cycle. Several executions of
 * one task on one day coexist; a requestId makes a retried request idempotent.
 */
export async function createAdhocOccurrence(ctx: AuditContext, input: AdhocOccurrenceInput): Promise<AdhocResult> {
  const settings = await requireSettings(ctx);
  const done = input.done ?? false;
  const identity: RequestIdentity = { taskId: input.taskId, name: '', date: input.date, done };

  if (input.requestId) {
    const existing = await findOccurrenceByRequestId(ctx.db, input.requestId);
    if (existing) return syncAdhocPoints(ctx, replayOrConflict(existing, identity, settings.timezone));
  }

  const task = await findTaskById(ctx.db, input.taskId);
  if (!task?.active) {
    throw new HttpError(400, 'validation_error', 'Invalid task', [
      { field: 'taskId', message: task ? 'inactive_task' : 'unknown_task' },
    ]);
  }
  assertRecordedWorkRules(ctx, settings, { date: input.date, done, assigneeId: input.assigneeId });
  const room = await findRoomById(ctx.db, task.roomId);
  const assigneeId =
    input.assigneeId === undefined ? (done ? ctx.actorId : task.defaultAssigneeId) : input.assigneeId;
  await assertActiveAssignee(ctx, assigneeId);
  const cycle = await requireGeneratedCycle(ctx, settings, input.date);

  const now = ctx.clock.now();
  const date = fromDayKey(input.date, settings.timezone);
  const warnings: ApiWarning[] = [];
  // Also when recording it as done: the client can then offer to check off the planned occurrence instead.
  const planned = await findOccurrences(ctx.db, { taskId: task._id, status: 'open', date });
  if (planned.length > 0) {
    warnings.push({
      code: 'task_already_planned',
      message: 'This task is already planned on that day',
      details: { taskId: task._id.toHexString(), date: input.date },
    });
  }

  const doc: OccurrenceDoc = {
    _id: new ObjectId(),
    taskId: task._id,
    cycleId: cycle._id,
    planId: null,
    date,
    plannedDate: date,
    assigneeId,
    status: done ? 'done' : 'open',
    statusBeforeCompletion: null,
    completedAt: done ? now : null,
    completedBy: done ? assigneeId : null,
    skipReason: null,
    durationMinutesSnapshot: task.durationMinutes,
    taskNameSnapshot: task.name,
    roomIdSnapshot: task.roomId,
    roomNameSnapshot: room?.name ?? null,
    origin: 'adhoc',
    recordedDone: done,
    requestId: input.requestId ?? null,
    ...(done ? { pointsSnapshot: taskPoints(task) } : {}),
    createdAt: now,
    updatedAt: now,
  };
  const result = await insertAdhoc(ctx, settings, doc, 'extra', identity, warnings);
  if (result.created && done) await refreshLastCompletedAt(ctx, task._id, doc._id);
  await syncAdhocPoints(ctx, result);
  return result;
}

/**
 * A one-off task (ADR-0009): an ad-hoc occurrence without a task record. Name, duration and
 * room live in the snapshot fields only, so it never shows up in the task list, the due
 * list, the planner or the AI input. Same idempotency and recorded-work rules as an extra
 * execution; a missing room is stored as null.
 */
export async function createOneOffOccurrence(ctx: AuditContext, input: OneOffOccurrenceInput): Promise<AdhocResult> {
  const settings = await requireSettings(ctx);
  const done = input.done ?? false;
  const identity: RequestIdentity = { taskId: null, name: input.name, date: input.date, done };

  if (input.requestId) {
    const existing = await findOccurrenceByRequestId(ctx.db, input.requestId);
    if (existing) return syncAdhocPoints(ctx, replayOrConflict(existing, identity, settings.timezone));
  }

  const room = input.roomId ? await findRoomById(ctx.db, input.roomId) : null;
  if (input.roomId && !room?.active) {
    throw new HttpError(400, 'validation_error', 'Invalid room', [
      { field: 'roomId', message: room ? 'inactive_room' : 'unknown_room' },
    ]);
  }
  assertRecordedWorkRules(ctx, settings, { date: input.date, done, assigneeId: input.assigneeId });
  const assigneeId = input.assigneeId === undefined ? (done ? ctx.actorId : null) : input.assigneeId;
  await assertActiveAssignee(ctx, assigneeId);
  const cycle = await requireGeneratedCycle(ctx, settings, input.date);

  const now = ctx.clock.now();
  const date = fromDayKey(input.date, settings.timezone);
  const doc: OccurrenceDoc = {
    _id: new ObjectId(),
    taskId: null,
    cycleId: cycle._id,
    planId: null,
    date,
    plannedDate: date,
    assigneeId,
    status: done ? 'done' : 'open',
    statusBeforeCompletion: null,
    completedAt: done ? now : null,
    completedBy: done ? assigneeId : null,
    skipReason: null,
    durationMinutesSnapshot: input.durationMinutes,
    taskNameSnapshot: input.name,
    roomIdSnapshot: room?._id ?? null,
    roomNameSnapshot: room?.name ?? null,
    origin: 'adhoc',
    recordedDone: done,
    requestId: input.requestId ?? null,
    // A one-off task has no task value: the duration rule applies (ADR-0011).
    ...(done ? { pointsSnapshot: defaultPointsForDuration(input.durationMinutes) } : {}),
    createdAt: now,
    updatedAt: now,
  };
  const result = await insertAdhoc(ctx, settings, doc, 'one_off', identity, []);
  await syncAdhocPoints(ctx, result);
  return result;
}

/** Sets the actor as assignee only while unassigned (atomic); otherwise 409. */
export async function claimOccurrence(ctx: AuditContext, id: ObjectId): Promise<OccurrenceDoc> {
  const result = await updateOccurrence(
    ctx,
    id,
    { assigneeId: ctx.actorId },
    { action: 'assign', meta: { claim: true } },
    { assigneeId: null },
  );
  if (result) return result.after;
  await requireOccurrence(ctx, id);
  throw new HttpError(409, 'already_claimed', 'Occurrence already has an assignee');
}
