import { ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { diffFields, isEmptyDiff } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { diffSlots, sortSlots } from '../domain/slots.ts';
import { COLLECTIONS } from './db.ts';

export interface SlotDoc {
  taskId: ObjectId;
  weekIndex: number;
  /** 0=Sunday..6=Saturday */
  weekday: number;
  assigneeId: ObjectId | null;
  sortOrder: number;
}

export type WeekThemes = [string, string, string, string];

export interface CyclePlanDoc {
  _id: ObjectId;
  name: string;
  active: boolean;
  slots: SlotDoc[];
  weekThemes: WeekThemes;
  draft: boolean;
  source: 'manual' | 'ai';
  proposalId: string | null;
  rationale: WeekThemes | null;
  discarded: boolean;
  createdAt: Date;
  updatedAt: Date;
}

export type NewPlan = Omit<CyclePlanDoc, '_id' | 'createdAt' | 'updatedAt'>;

export const EMPTY_WEEK_THEMES: WeekThemes = ['', '', '', ''];

const plansCollection = (db: Db) => db.collection<CyclePlanDoc>(COLLECTIONS.cyclePlans);

export function findPlanById(db: Db, id: ObjectId): Promise<CyclePlanDoc | null> {
  return plansCollection(db).findOne({ _id: id });
}

export function findActivePlan(db: Db): Promise<CyclePlanDoc | null> {
  return plansCollection(db).findOne({ active: true });
}

export function listPlans(db: Db): Promise<CyclePlanDoc[]> {
  return plansCollection(db).find({}).sort({ createdAt: 1, _id: 1 }).toArray();
}

export function countPlans(db: Db): Promise<number> {
  return plansCollection(db).countDocuments();
}

export async function createPlan(
  ctx: AuditContext,
  input: NewPlan,
  meta?: Record<string, unknown>,
): Promise<CyclePlanDoc> {
  const now = ctx.clock.now();
  const doc: CyclePlanDoc = {
    _id: new ObjectId(),
    ...input,
    slots: sortSlots(input.slots),
    createdAt: now,
    updatedAt: now,
  };
  await plansCollection(ctx.db).insertOne(doc);
  const { after } = diffFields({}, { ...doc }, { ignore: ['_id', 'createdAt', 'updatedAt'] });
  await record(ctx, { entity: 'cyclePlan', entityId: doc._id, action: 'create', after, ...(meta ? { meta } : {}) });
  return doc;
}

/** Permanently deletes one plan and records the removed plan in the audit log. */
export async function deletePlan(ctx: AuditContext, id: ObjectId): Promise<CyclePlanDoc | null> {
  const before = await findPlanById(ctx.db, id);
  if (!before) return null;
  const result = await plansCollection(ctx.db).deleteOne({ _id: id });
  if (result.deletedCount !== 1) return null;
  const { before: auditBefore } = diffFields(
    { ...before },
    {},
    { ignore: ['_id', 'createdAt', 'updatedAt'] },
  );
  await record(ctx, {
    entity: 'cyclePlan',
    entityId: id,
    action: 'delete',
    before: auditBefore,
  });
  return before;
}

/** Name and week themes. Returns null if the plan does not exist. */
export async function updatePlanMeta(
  ctx: AuditContext,
  id: ObjectId,
  patch: Partial<Pick<CyclePlanDoc, 'name' | 'weekThemes'>>,
): Promise<CyclePlanDoc | null> {
  const before = await findPlanById(ctx.db, id);
  if (!before) return null;
  const diff = diffFields({ ...before }, { ...before, ...patch });
  if (isEmptyDiff(diff)) return before;
  const after = await plansCollection(ctx.db).findOneAndUpdate(
    { _id: id },
    { $set: { ...patch, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return null;
  await record(ctx, { entity: 'cyclePlan', entityId: id, action: 'update', ...diff });
  return after;
}

/**
 * Makes exactly this plan active. Other active plans are deactivated (audited
 * as 'update'); the plan itself gets an 'activate' entry. Returns null if missing.
 */
export async function setActivePlan(
  ctx: AuditContext,
  id: ObjectId,
  meta: Record<string, unknown>,
  options: { action?: 'activate' | 'ai-apply'; changes?: Partial<Pick<CyclePlanDoc, 'draft'>> } = {},
): Promise<CyclePlanDoc | null> {
  const plan = await findPlanById(ctx.db, id);
  if (!plan) return null;
  const now = ctx.clock.now();
  const action = options.action ?? 'activate';
  const changes = options.changes ?? {};

  const others = await plansCollection(ctx.db).find({ active: true, _id: { $ne: id } }).toArray();
  if (others.length > 0) {
    await plansCollection(ctx.db).updateMany(
      { _id: { $in: others.map((p) => p._id) } },
      { $set: { active: false, updatedAt: now } },
    );
    for (const other of others) {
      await record(ctx, {
        entity: 'cyclePlan',
        entityId: other._id,
        action: 'update',
        before: { active: true },
        after: { active: false },
        meta: { ...meta, activatedPlanId: id },
      });
    }
  }

  const after = await plansCollection(ctx.db).findOneAndUpdate(
    { _id: id },
    { $set: { active: true, ...changes, updatedAt: now } },
    { returnDocument: 'after' },
  );
  if (!after) return null;
  const changedDraft = changes.draft !== undefined && changes.draft !== plan.draft;
  await record(ctx, {
    entity: 'cyclePlan',
    entityId: id,
    action,
    before: { active: plan.active, ...(changedDraft ? { draft: plan.draft } : {}) },
    after: { active: true, ...(changedDraft ? { draft: changes.draft } : {}) },
    meta,
  });
  return after;
}

/** Marks a draft as discarded (and inactive). Returns null if missing; no write when already discarded. */
export async function discardPlan(ctx: AuditContext, id: ObjectId): Promise<CyclePlanDoc | null> {
  const before = await findPlanById(ctx.db, id);
  if (!before) return null;
  if (before.discarded && !before.active) return before;
  const after = await plansCollection(ctx.db).findOneAndUpdate(
    { _id: id },
    { $set: { active: false, discarded: true, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return null;
  await record(ctx, {
    entity: 'cyclePlan',
    entityId: id,
    action: 'update',
    before: { active: before.active, discarded: before.discarded },
    after: { active: false, discarded: true },
    ...(before.proposalId ? { meta: { proposalId: before.proposalId } } : {}),
  });
  return after;
}

/**
 * Replaces all slots. The audit entry holds only added, removed and changed
 * slots (keyed on taskId + weekIndex + weekday). Callers validate first.
 */
export async function replaceSlots(
  ctx: AuditContext,
  id: ObjectId,
  slots: SlotDoc[],
  meta?: Record<string, unknown>,
): Promise<CyclePlanDoc | null> {
  const before = await findPlanById(ctx.db, id);
  if (!before) return null;
  const next = sortSlots(slots);
  const diff = diffSlots(before.slots, next);
  if (diff.added.length === 0 && diff.removed.length === 0 && diff.changed.length === 0) return before;

  const after = await plansCollection(ctx.db).findOneAndUpdate(
    { _id: id },
    { $set: { slots: next, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return null;
  await record(ctx, {
    entity: 'cyclePlan',
    entityId: id,
    action: 'update',
    before: { slots: [...diff.removed, ...diff.changed.map((c) => c.before)] },
    after: { slots: [...diff.added, ...diff.changed.map((c) => c.after)] },
    ...(meta ? { meta } : {}),
  });
  return after;
}

/** Removes a deleted task from every plan, preserving an auditable slot diff. */
export async function removeTaskFromPlans(ctx: AuditContext, taskId: ObjectId): Promise<number> {
  const plans = await plansCollection(ctx.db).find({ 'slots.taskId': taskId }).toArray();
  for (const plan of plans) {
    await replaceSlots(
      ctx,
      plan._id,
      plan.slots.filter((slot) => !slot.taskId.equals(taskId)),
      { reason: 'task_delete', taskId },
    );
  }
  return plans.length;
}
