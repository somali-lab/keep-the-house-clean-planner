import {
  cycleIndexFor,
  fromDayKey,
  toDayKey,
  weekdaySun0,
  type ApiWarning,
  type OccurrenceView,
} from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { findCycleByIndex } from '../data/cycles.ts';
import {
  findOccurrenceById,
  findOccurrences,
  insertOccurrencesIdempotent,
  updateOccurrence,
  type OccurrenceDoc,
} from '../data/occurrences.ts';
import { getSettings } from '../data/settings.ts';
import { findTaskById, setTaskLastCompletedAt } from '../data/tasks.ts';
import { findRoomById } from '../data/rooms.ts';
import { findUserById } from '../data/users.ts';
import { HttpError, notFound } from '../http/errors.ts';
import { toApi } from '../http/serialize.ts';

/** API view: calendar dates as day keys, plus derived overdue and drag info. */
export function toOccurrenceView(doc: OccurrenceDoc, todayKey: string, timezone: string): OccurrenceView {
  const date = toDayKey(doc.date, timezone);
  const plannedDate = toDayKey(doc.plannedDate, timezone);
  return {
    ...toApi(doc),
    date,
    plannedDate,
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
async function refreshLastCompletedAt(ctx: AuditContext, taskId: ObjectId, occurrenceId: ObjectId): Promise<void> {
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
): Promise<OccurrenceDoc> {
  const current = await requireOccurrence(ctx, id);
  if (current.status === 'done') throw invalidTransition('done', 'complete');

  // A normal check-off belongs to the person the task was planned for, even
  // when another profile presses the button. Unassigned work belongs to the
  // actor; the explicit completedBy option can still override either case.
  const completedBy = completedByInput ?? current.assigneeId ?? ctx.actorId;
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
  const result = await updateOccurrence(
    ctx,
    id,
    {
      status: 'done',
      statusBeforeCompletion: current.status,
      completedAt: ctx.clock.now(),
      completedBy,
      // Completing an unclaimed occurrence claims it for whoever did it.
      ...(claimed ? { assigneeId: completedBy } : {}),
    },
    { action: 'complete', meta: { completedBy, wasAssignee, ...(claimed ? { claimed: true } : {}) } },
    { status: current.status },
  );
  if (!result) throw invalidTransition('changed', 'complete');
  await refreshLastCompletedAt(ctx, current.taskId, id);
  return result.after;
}

export async function uncompleteOccurrence(ctx: AuditContext, id: ObjectId): Promise<OccurrenceDoc> {
  const current = await requireOccurrence(ctx, id);
  if (current.status !== 'done') throw invalidTransition(current.status, 'uncomplete');
  const result = await updateOccurrence(
    ctx,
    id,
    {
      status: current.statusBeforeCompletion ?? 'open',
      statusBeforeCompletion: null,
      completedAt: null,
      completedBy: null,
    },
    { action: 'uncomplete' },
    { status: 'done' },
  );
  if (!result) throw invalidTransition('changed', 'uncomplete');
  await refreshLastCompletedAt(ctx, current.taskId, id);
  return result.after;
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
  /** undefined = the task's default assignee; null = "wie dan ook". */
  assigneeId?: ObjectId | null;
}

/**
 * Plans a task on a day outside the template (e.g. a quarterly task from the
 * due list). Only within cycles that are already generated, so exports and
 * generation never see a half-filled cycle. One per task per day (409).
 */
export async function createAdhocOccurrence(ctx: AuditContext, input: AdhocOccurrenceInput): Promise<OccurrenceDoc> {
  const settings = await getSettings(ctx.db);
  if (!settings) throw new HttpError(500, 'settings_missing');

  const task = await findTaskById(ctx.db, input.taskId);
  if (!task?.active) {
    throw new HttpError(400, 'validation_error', 'Invalid task', [
      { field: 'taskId', message: task ? 'inactive_task' : 'unknown_task' },
    ]);
  }
  const room = await findRoomById(ctx.db, task.roomId);
  const assigneeId = input.assigneeId === undefined ? task.defaultAssigneeId : input.assigneeId;
  if (assigneeId) {
    const user = await findUserById(ctx.db, assigneeId);
    if (!user?.active) {
      throw new HttpError(400, 'validation_error', 'Invalid assignee', [
        { field: 'assigneeId', message: user ? 'inactive_user' : 'unknown_user' },
      ]);
    }
  }

  const cycle = await findCycleByIndex(ctx.db, cycleIndexFor(input.date, settings.cycleAnchorDate));
  if (!cycle) {
    throw new HttpError(409, 'cycle_not_generated', 'That day is not generated yet', undefined, {
      date: input.date,
    });
  }

  const now = ctx.clock.now();
  const date = fromDayKey(input.date, settings.timezone);
  const doc: OccurrenceDoc = {
    _id: new ObjectId(),
    taskId: task._id,
    cycleId: cycle._id,
    planId: null,
    date,
    plannedDate: date,
    assigneeId,
    status: 'open',
    statusBeforeCompletion: null,
    completedAt: null,
    completedBy: null,
    skipReason: null,
    durationMinutesSnapshot: task.durationMinutes,
    taskNameSnapshot: task.name,
    roomIdSnapshot: task.roomId,
    roomNameSnapshot: room?.name ?? null,
    origin: 'adhoc',
    createdAt: now,
    updatedAt: now,
  };
  const [inserted] = await insertOccurrencesIdempotent(ctx, [doc], { origin: 'adhoc' });
  if (!inserted) {
    throw new HttpError(409, 'occurrence_exists', 'This task is already planned on that day');
  }
  return inserted;
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
