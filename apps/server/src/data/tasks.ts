import { ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { diffFields, isEmptyDiff, type FieldDiff } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { COLLECTIONS } from './db.ts';

export interface TaskDoc {
  _id: ObjectId;
  name: string;
  roomId: ObjectId;
  intervalKey: string;
  durationMinutes: number;
  defaultAssigneeId: ObjectId | null;
  active: boolean;
  notes: string;
  tags: string[];
  lastCompletedAt: Date | null;
  createdAt: Date;
  updatedAt: Date;
}

export type NewTask = Pick<
  TaskDoc,
  'name' | 'roomId' | 'intervalKey' | 'durationMinutes' | 'defaultAssigneeId' | 'notes' | 'tags'
>;

export type TaskPatch = Partial<NewTask & Pick<TaskDoc, 'active'>>;

const CREATE_IGNORE = ['_id', 'createdAt', 'updatedAt'];

export const tasksCollection = (db: Db) => db.collection<TaskDoc>(COLLECTIONS.tasks);

export function findTaskById(db: Db, id: ObjectId): Promise<TaskDoc | null> {
  return tasksCollection(db).findOne({ _id: id });
}

export function listTasks(db: Db, filter: { roomId?: ObjectId; active?: boolean } = {}): Promise<TaskDoc[]> {
  return tasksCollection(db)
    .find({
      ...(filter.roomId ? { roomId: filter.roomId } : {}),
      ...(filter.active === undefined ? {} : { active: filter.active }),
    })
    .sort({ name: 1, _id: 1 })
    .toArray();
}

/** Interval keys referenced by any task, active or not (history keeps pointing at them). */
export function intervalKeysInUse(db: Db): Promise<string[]> {
  return tasksCollection(db).distinct('intervalKey');
}

export function countTasksInRoom(db: Db, roomId: ObjectId): Promise<number> {
  return tasksCollection(db).countDocuments({ roomId });
}

/** Stores a task. Callers validate references (room, interval, assignee) first. */
export async function createTask(ctx: AuditContext, input: NewTask): Promise<TaskDoc> {
  const now = ctx.clock.now();
  const doc: TaskDoc = {
    _id: new ObjectId(),
    ...input,
    active: true,
    lastCompletedAt: null,
    createdAt: now,
    updatedAt: now,
  };
  await tasksCollection(ctx.db).insertOne(doc);
  const { after } = diffFields({}, { ...doc }, { ignore: CREATE_IGNORE });
  await record(ctx, { entity: 'task', entityId: doc._id, action: 'create', after });
  return doc;
}

/** Permanently removes a task after callers have removed it from cycle plans. */
export async function deleteTask(ctx: AuditContext, id: ObjectId): Promise<TaskDoc | null> {
  const before = await findTaskById(ctx.db, id);
  if (!before) return null;
  const result = await tasksCollection(ctx.db).deleteOne({ _id: id });
  if (result.deletedCount !== 1) return null;
  const { before: auditBefore } = diffFields(
    { ...before },
    {},
    { ignore: ['_id', 'createdAt', 'updatedAt'] },
  );
  await record(ctx, { entity: 'task', entityId: id, action: 'delete', before: auditBefore });
  return before;
}

/** A change of the default assignee is its own 'assign' entry; other fields go into 'update'. */
async function recordTaskChange(ctx: AuditContext, id: ObjectId, diff: FieldDiff): Promise<void> {
  const { defaultAssigneeId: _beforeAssignee, ...beforeRest } = diff.before;
  const { defaultAssigneeId: _afterAssignee, ...afterRest } = diff.after;
  if (Object.keys(beforeRest).length > 0 || Object.keys(afterRest).length > 0) {
    await record(ctx, { entity: 'task', entityId: id, action: 'update', before: beforeRest, after: afterRest });
  }
  if ('defaultAssigneeId' in diff.before || 'defaultAssigneeId' in diff.after) {
    await record(ctx, {
      entity: 'task',
      entityId: id,
      action: 'assign',
      before: { defaultAssigneeId: diff.before.defaultAssigneeId ?? null },
      after: { defaultAssigneeId: diff.after.defaultAssigneeId ?? null },
    });
  }
}

/** Returns null if the task does not exist; skips the write (and audit) when nothing changes. */
export async function updateTask(ctx: AuditContext, id: ObjectId, patch: TaskPatch): Promise<TaskDoc | null> {
  const before = await findTaskById(ctx.db, id);
  if (!before) return null;
  const diff = diffFields({ ...before }, { ...before, ...patch });
  if (isEmptyDiff(diff)) return before;

  const after = await tasksCollection(ctx.db).findOneAndUpdate(
    { _id: id },
    { $set: { ...patch, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return null;
  await recordTaskChange(ctx, id, diff);
  return after;
}

/** Maintains the denormalised `lastCompletedAt`; audited, no-op when unchanged. */
export async function setTaskLastCompletedAt(
  ctx: AuditContext,
  id: ObjectId,
  value: Date | null,
  meta: Record<string, unknown>,
): Promise<void> {
  const before = await findTaskById(ctx.db, id);
  if (!before) return;
  if ((before.lastCompletedAt?.getTime() ?? null) === (value?.getTime() ?? null)) return;
  await tasksCollection(ctx.db).updateOne(
    { _id: id },
    { $set: { lastCompletedAt: value, updatedAt: ctx.clock.now() } },
  );
  await record(ctx, {
    entity: 'task',
    entityId: id,
    action: 'update',
    before: { lastCompletedAt: before.lastCompletedAt },
    after: { lastCompletedAt: value },
    meta,
  });
}

/**
 * Applies the same change to every active task in a room, with one audit entry
 * per changed task. Returns the number of tasks that actually changed.
 */
export async function bulkUpdateRoomTasks(
  ctx: AuditContext,
  roomId: ObjectId,
  change: { active: false } | { defaultAssigneeId: ObjectId | null },
): Promise<number> {
  const tasks = await tasksCollection(ctx.db).find({ roomId, active: true }).toArray();
  const changed = tasks
    .map((task) => ({ task, diff: diffFields({ ...task }, { ...task, ...change }) }))
    .filter(({ diff }) => !isEmptyDiff(diff));
  if (changed.length === 0) return 0;

  await tasksCollection(ctx.db).updateMany(
    { _id: { $in: changed.map(({ task }) => task._id) } },
    { $set: { ...change, updatedAt: ctx.clock.now() } },
  );
  for (const { task, diff } of changed) {
    await recordTaskChange(ctx, task._id, diff);
  }
  return changed.length;
}
