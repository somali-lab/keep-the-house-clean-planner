import type { BadgeImageType, BadgeRuleType } from '@huishoudplanner/shared';
import { Binary, MongoServerError, ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { diffFields, isEmptyDiff } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { COLLECTIONS } from './db.ts';

/** The stored image of a badge: the bytes and what identifies them (ADR-0014). */
export interface BadgeImageDoc {
  data: Binary;
  contentType: BadgeImageType;
  size: number;
  /** Hex SHA-256 of the bytes. */
  hash: string;
}

export type BadgeRuleDoc =
  | { type: Extract<BadgeRuleType, 'executions' | 'minutes'>; taskIds: ObjectId[]; threshold: number }
  | { type: 'onTimeWeeks'; threshold: number };

/** A badge definition. Awards are derived from it and the executions; see `BadgeAwardDoc`. */
export interface BadgeDoc {
  _id: ObjectId;
  name: string;
  description: string;
  rule: BadgeRuleDoc;
  active: boolean;
  /** Set on an example badge; unique, so the example action is idempotent. */
  exampleKey?: string | null;
  image?: BadgeImageDoc | null;
  createdAt: Date;
  updatedAt: Date;
}

/** One derived award: a person earned a badge. The key makes it idempotent. */
export interface BadgeAwardDoc {
  _id: ObjectId;
  /** Unique; 'badge:<badgeId>:<personId>'. */
  key: string;
  badgeId: ObjectId;
  personId: ObjectId;
  /** The moment the data first crossed the threshold, not when it was computed. */
  awardedAt: Date;
  createdAt: Date;
  updatedAt: Date;
}

/** Fixed id of the awards as a whole, the audit entityId of a bulk reconciliation summary (like POINTS_LEDGER_ID). */
export const BADGE_AWARDS_LEDGER_ID = new ObjectId('000000000000000000000003');

export const badgesCollection = (db: Db) => db.collection<BadgeDoc>(COLLECTIONS.badges);
export const badgeAwardsCollection = (db: Db) => db.collection<BadgeAwardDoc>(COLLECTIONS.badgeAwards);

/** Ids in a stable order, so the tasks of a rule compare as a set. */
export function sortedIds(ids: ObjectId[]): ObjectId[] {
  return [...ids].sort((a, b) => (a.toHexString() < b.toHexString() ? -1 : a.toHexString() > b.toHexString() ? 1 : 0));
}

export function countBadges(db: Db): Promise<number> {
  return badgesCollection(db).countDocuments();
}

export const badgeAwardKey = (badgeId: ObjectId, personId: ObjectId): string =>
  `badge:${badgeId.toHexString()}:${personId.toHexString()}`;

/** The bytes of a stored image. */
export function imageBytes(image: BadgeImageDoc): Buffer {
  return Buffer.from(image.data.buffer.subarray(0, image.data.position));
}

export function findBadgeById(db: Db, id: ObjectId): Promise<BadgeDoc | null> {
  return badgesCollection(db).findOne({ _id: id });
}

export function findBadgeByExampleKey(db: Db, exampleKey: string): Promise<BadgeDoc | null> {
  return badgesCollection(db).findOne({ exampleKey });
}

/** Oldest first, so the examples keep the order they were added in. */
export function listBadges(db: Db, filter: { active?: boolean } = {}): Promise<BadgeDoc[]> {
  const query = filter.active === undefined ? {} : { active: filter.active };
  return badgesCollection(db).find(query).sort({ createdAt: 1, _id: 1 }).toArray();
}

/**
 * What an audit entry says about a badge: everything except the bytes. An image is recorded by its
 * type, size and hash, so a changed picture is visible without storing it in the log (ADR-0014).
 */
export function badgeAuditView(doc: BadgeDoc): Record<string, unknown> {
  return {
    name: doc.name,
    description: doc.description,
    active: doc.active,
    rule: doc.rule.type === 'onTimeWeeks' ? doc.rule : { ...doc.rule, taskIds: sortedIds(doc.rule.taskIds) },
    exampleKey: doc.exampleKey ?? null,
    image: doc.image ? { contentType: doc.image.contentType, size: doc.image.size, hash: doc.image.hash } : null,
  };
}

export type NewBadge = Pick<BadgeDoc, 'name' | 'description' | 'rule' | 'active'> & {
  exampleKey?: string | null;
  image?: BadgeImageDoc | null;
};

export async function createBadge(ctx: AuditContext, input: NewBadge, meta?: Record<string, unknown>): Promise<BadgeDoc> {
  const now = ctx.clock.now();
  const doc: BadgeDoc = {
    _id: new ObjectId(),
    name: input.name,
    description: input.description,
    rule: input.rule,
    active: input.active,
    exampleKey: input.exampleKey ?? null,
    image: input.image ?? null,
    createdAt: now,
    updatedAt: now,
  };
  await badgesCollection(ctx.db).insertOne(doc);
  const { after } = diffFields({}, badgeAuditView(doc), { ignore: [] });
  await record(ctx, { entity: 'badge', entityId: doc._id, action: 'create', after, ...(meta ? { meta } : {}) });
  return doc;
}

/** Inserts an example badge unless one with its key exists; a duplicate key (a concurrent call won) reports null and writes nothing. */
export async function createExampleBadge(ctx: AuditContext, input: NewBadge & { exampleKey: string }): Promise<BadgeDoc | null> {
  try {
    return await createBadge(ctx, input, { example: input.exampleKey });
  } catch (err) {
    if (err instanceof MongoServerError && err.code === 11000) return null;
    throw err;
  }
}

export type BadgePatch = Partial<Pick<BadgeDoc, 'name' | 'description' | 'rule' | 'active'>> & { image?: BadgeImageDoc | null };

export interface BadgeUpdate {
  before: BadgeDoc;
  after: BadgeDoc;
  /** Whether the rule or the active flag changed: the awards must be recomputed. */
  affectsAwards: boolean;
}

/** Null when the badge does not exist. A patch that changes nothing writes and audits nothing. */
export async function updateBadge(ctx: AuditContext, id: ObjectId, patch: BadgePatch, meta?: Record<string, unknown>): Promise<BadgeUpdate | null> {
  const before = await findBadgeById(ctx.db, id);
  if (!before) return null;
  const next: BadgeDoc = { ...before, ...patch };
  const diff = diffFields(badgeAuditView(before), badgeAuditView(next), { ignore: [] });
  if (isEmptyDiff(diff)) return { before, after: before, affectsAwards: false };

  const after = await badgesCollection(ctx.db).findOneAndUpdate(
    { _id: id },
    { $set: { ...patch, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return null;
  await record(ctx, { entity: 'badge', entityId: id, action: 'update', ...diff, ...(meta ? { meta } : {}) });
  return { before, after, affectsAwards: 'rule' in diff.after || 'active' in diff.after };
}

/** Null when it is already gone. The awards of the badge are removed by the reconciliation that follows. */
export async function deleteBadge(ctx: AuditContext, id: ObjectId): Promise<BadgeDoc | null> {
  const deleted = await badgesCollection(ctx.db).findOneAndDelete({ _id: id });
  if (!deleted) return null;
  const { before } = diffFields(badgeAuditView(deleted), {}, { ignore: [] });
  await record(ctx, { entity: 'badge', entityId: id, action: 'delete', before });
  return deleted;
}

/**
 * A deleted task leaves the rules that named it (ADR-0014): its id is removed, and a rule that named tasks
 * and now names none is deactivated, because an empty list would count every task. Each change is an audited
 * badge update. Returns whether any badge changed, so the caller recomputes the awards.
 */
export async function removeTaskFromBadges(ctx: AuditContext, taskId: ObjectId): Promise<boolean> {
  const affected = await badgesCollection(ctx.db).find({ 'rule.taskIds': taskId }).toArray();
  let changed = false;
  for (const badge of affected) {
    if (badge.rule.type === 'onTimeWeeks') continue;
    const taskIds = badge.rule.taskIds.filter((id) => !id.equals(taskId));
    const update = await updateBadge(
      ctx,
      badge._id,
      { rule: { ...badge.rule, taskIds }, ...(taskIds.length === 0 ? { active: false } : {}) },
      { reason: 'task_deleted', taskId },
    );
    if (update?.affectsAwards) changed = true;
  }
  return changed;
}

export function listBadgeAwards(db: Db, filter: { personId?: ObjectId; badgeId?: ObjectId } = {}): Promise<BadgeAwardDoc[]> {
  return badgeAwardsCollection(db).find(filter).sort({ awardedAt: 1, _id: 1 }).toArray();
}

export interface BadgeAwardChanges {
  inserts: { badgeId: ObjectId; personId: ObjectId; awardedAt: Date }[];
  updates: { current: BadgeAwardDoc; awardedAt: Date }[];
  deletes: BadgeAwardDoc[];
}

export interface AppliedBadgeAwardChange {
  change: 'created' | 'updated' | 'removed';
  key: string;
  badgeId: ObjectId;
  /** The badge's name at that moment, so history can still name a badge that was deleted since. */
  badgeName: string;
  personId: ObjectId;
  awardedAt: Date;
}

const AWARD_AUDIT_IGNORE = ['_id', 'createdAt', 'updatedAt'];

/**
 * Applies the differences between the expected and the stored awards, one write each. Updates and
 * deletes are compare-and-set on the award that was read, and a duplicate key on an insert (it
 * already exists) is skipped, so a change that did not happen is neither reported nor audited. With
 * `audit` every real change writes its own entry (entity `badgeAward`, meta `{ reason }`); without it
 * the caller records one summary of the bulk run (ADR-0014).
 */
export async function applyBadgeAwardChanges(
  ctx: AuditContext,
  changes: BadgeAwardChanges,
  names: ReadonlyMap<string, string>,
  audit: { reason: string } | null,
): Promise<AppliedBadgeAwardChange[]> {
  const collection = badgeAwardsCollection(ctx.db);
  const applied: AppliedBadgeAwardChange[] = [];
  const metaOf = (badgeId: ObjectId) => ({ reason: audit!.reason, badgeName: names.get(badgeId.toHexString()) ?? '' });
  const nameOf = (badgeId: ObjectId) => names.get(badgeId.toHexString()) ?? '';

  for (const current of changes.deletes) {
    const deleted = await collection.findOneAndDelete({ _id: current._id, awardedAt: current.awardedAt });
    if (!deleted) continue;
    applied.push({ change: 'removed', key: deleted.key, badgeId: deleted.badgeId, badgeName: nameOf(deleted.badgeId), personId: deleted.personId, awardedAt: deleted.awardedAt });
    if (audit) {
      const { before } = diffFields({ ...deleted }, {}, { ignore: AWARD_AUDIT_IGNORE });
      await record(ctx, { entity: 'badgeAward', entityId: deleted._id, action: 'delete', before, meta: metaOf(deleted.badgeId) });
    }
  }
  for (const { current, awardedAt } of changes.updates) {
    const after = await collection.findOneAndUpdate(
      { _id: current._id, awardedAt: current.awardedAt },
      { $set: { awardedAt, updatedAt: ctx.clock.now() } },
      { returnDocument: 'after' },
    );
    if (!after) continue;
    applied.push({ change: 'updated', key: after.key, badgeId: after.badgeId, badgeName: nameOf(after.badgeId), personId: after.personId, awardedAt: after.awardedAt });
    if (audit) {
      const diff = diffFields({ ...current }, { ...after }, { ignore: AWARD_AUDIT_IGNORE });
      await record(ctx, { entity: 'badgeAward', entityId: after._id, action: 'update', ...diff, meta: { ...metaOf(after.badgeId), badgeId: after.badgeId, personId: after.personId } });
    }
  }
  for (const { badgeId, personId, awardedAt } of changes.inserts) {
    const now = ctx.clock.now();
    const doc: BadgeAwardDoc = { _id: new ObjectId(), key: badgeAwardKey(badgeId, personId), badgeId, personId, awardedAt, createdAt: now, updatedAt: now };
    try {
      await collection.insertOne(doc);
    } catch (err) {
      if (err instanceof MongoServerError && err.code === 11000) continue;
      throw err;
    }
    applied.push({ change: 'created', key: doc.key, badgeId, badgeName: nameOf(badgeId), personId, awardedAt });
    if (audit) {
      const { after } = diffFields({}, { ...doc }, { ignore: AWARD_AUDIT_IGNORE });
      await record(ctx, { entity: 'badgeAward', entityId: doc._id, action: 'create', after, meta: metaOf(badgeId) });
    }
  }
  return applied;
}

/** Removes every award and returns how many went; an import rebuilds them from the imported data (ADR-0014). */
export async function clearBadgeAwards(db: Db): Promise<number> {
  return (await badgeAwardsCollection(db).deleteMany({})).deletedCount;
}
