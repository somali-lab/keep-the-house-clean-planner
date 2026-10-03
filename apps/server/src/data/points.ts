import { MongoBulkWriteError, MongoServerError, ObjectId, type AnyBulkWriteOperation, type Db } from 'mongodb';
import { BONUS_KINDS, type PointEntryKind, type PointEntrySource, type PointsSyncReason } from '@huishoudplanner/shared';
import type { AuditContext } from '../audit/context.ts';
import { diffFields, isEmptyDiff } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { COLLECTIONS } from './db.ts';

/**
 * One entry of the points ledger (ADR-0011, ADR-0012). An entry of kind 'execution' is derived from
 * one occurrence; the four bonus kinds are derived from the occurrences of a person in one period.
 */
export interface PointEntryDoc {
  _id: ObjectId;
  /** Unique; 'execution:<occurrenceId>'. */
  key: string;
  kind: PointEntryKind;
  personId: ObjectId;
  /** Signed integer; an execution is >= 1. */
  amount: number;
  /** Local midnight of the occurrence's date. */
  date: Date;
  /** Local midnight of that week's Monday. */
  weekStart: Date;
  /** Local midnight of the first day of the week or cycle a bonus is for; null or missing for an execution. */
  periodStart?: Date | null;
  occurrenceId: ObjectId | null;
  /** Null for a one-off task. */
  taskId: ObjectId | null;
  /** The occurrence's task name snapshot, so the entry stays readable. */
  titleSnapshot: string;
  /** The path that wrote the current value. */
  source: PointEntrySource;
  createdAt: Date;
  updatedAt: Date;
}

/** The fields a sync derives from an occurrence; everything else is bookkeeping. */
export type PointEntryFields = Pick<
  PointEntryDoc,
  'personId' | 'amount' | 'date' | 'weekStart' | 'occurrenceId' | 'taskId' | 'titleSnapshot'
> & { periodStart?: Date | null };

/** Fixed id of the ledger as a whole, the audit entityId of a reconciliation summary (like SETTINGS_ID). */
export const POINTS_LEDGER_ID = new ObjectId('000000000000000000000002');

const AUDIT_IGNORE = ['_id', 'createdAt', 'updatedAt'];

export const pointEntriesCollection = (db: Db) => db.collection<PointEntryDoc>(COLLECTIONS.pointEntries);

export const executionKey = (occurrenceId: ObjectId): string => `execution:${occurrenceId.toHexString()}`;

/** Kinds that are derived from the occurrences; the statistics reset removes these and never another kind. */
const DERIVED_KINDS: PointEntryKind[] = ['execution', ...BONUS_KINDS];

export function findPointEntryByKey(db: Db, key: string): Promise<PointEntryDoc | null> {
  return pointEntriesCollection(db).findOne({ key });
}

export function findPointEntries(db: Db, filter: Partial<Pick<PointEntryDoc, 'personId' | 'kind'>> = {}): Promise<PointEntryDoc[]> {
  return pointEntriesCollection(db).find(filter).sort({ date: -1, _id: 1 }).toArray();
}

/** Every stored week and cycle bonus entry (ADR-0012). */
export function findBonusPointEntries(db: Db): Promise<PointEntryDoc[]> {
  return pointEntriesCollection(db).find({ kind: { $in: [...BONUS_KINDS] } }).sort({ date: -1, _id: 1 }).toArray();
}

interface AuditMeta {
  occurrenceId: ObjectId;
  reason: PointsSyncReason;
}

/**
 * Inserts a ledger entry and audits it. A duplicate key (a concurrent sync created it first)
 * is reported as `inserted: false`, writing and auditing nothing, so the caller can update instead.
 */
export async function insertPointEntry(
  ctx: AuditContext,
  key: string,
  kind: PointEntryKind,
  fields: PointEntryFields,
  source: PointEntrySource,
  meta: AuditMeta,
): Promise<{ inserted: true; doc: PointEntryDoc } | { inserted: false }> {
  const now = ctx.clock.now();
  const doc: PointEntryDoc = { _id: new ObjectId(), key, kind, ...fields, periodStart: fields.periodStart ?? null, source, createdAt: now, updatedAt: now };
  try {
    await pointEntriesCollection(ctx.db).insertOne(doc);
  } catch (err) {
    if (err instanceof MongoServerError && err.code === 11000) return { inserted: false };
    throw err;
  }
  const { after } = diffFields({}, { ...doc }, { ignore: AUDIT_IGNORE });
  await record(ctx, { entity: 'points', entityId: doc._id, action: 'create', after, meta: { ...meta } });
  return { inserted: true, doc };
}

/** Brings an existing entry to the given fields. A no-op writes and audits nothing. Returns whether it changed. */
export async function updatePointEntry(
  ctx: AuditContext,
  current: PointEntryDoc,
  fields: PointEntryFields,
  source: PointEntrySource,
  meta: AuditMeta,
): Promise<boolean> {
  const diff = diffFields({ ...current }, { ...current, ...fields }, { ignore: [...AUDIT_IGNORE, 'source', 'periodStart'] });
  if (isEmptyDiff(diff)) return false;
  // The diff holds changed fields only, so the title and amount travel in the meta for the history feed.
  const auditMeta = { ...meta, titleSnapshot: fields.titleSnapshot, amount: fields.amount };
  const after = await pointEntriesCollection(ctx.db).findOneAndUpdate(
    { _id: current._id },
    { $set: { ...fields, source, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return false;
  await record(ctx, { entity: 'points', entityId: current._id, action: 'update', ...diff, meta: auditMeta });
  return true;
}

/** Removes an entry and audits it. Returns false, writing and auditing nothing, when it is already gone. */
export async function deletePointEntry(ctx: AuditContext, current: PointEntryDoc, meta: AuditMeta): Promise<boolean> {
  const deleted = await pointEntriesCollection(ctx.db).findOneAndDelete({ _id: current._id });
  if (!deleted) return false;
  const { before } = diffFields({ ...deleted }, {}, { ignore: AUDIT_IGNORE });
  await record(ctx, { entity: 'points', entityId: deleted._id, action: 'delete', before, meta: { ...meta } });
  return true;
}

/** Filter that only matches the entry while it still has the values that were read. */
function sameAsRead(current: PointEntryDoc) {
  return { _id: current._id, personId: current.personId, amount: current.amount, date: current.date };
}

export interface PointEntryChanges {
  inserts: { key: string; kind: PointEntryKind; fields: PointEntryFields }[];
  updates: { current: PointEntryDoc; fields: PointEntryFields }[];
  deletes: PointEntryDoc[];
}

export interface AppliedPointEntryChanges {
  created: number;
  updated: number;
  removed: number;
}

/**
 * Applies a reconciliation as one unordered bulk write, without a per-entry audit entry: the
 * caller records one summary (ADR-0011). Created entries get source 'backfill', changed entries
 * 'recompute'. A duplicate key on an insert (a concurrent sync created it first) is skipped.
 */
export async function applyPointEntryChanges(
  ctx: AuditContext,
  changes: PointEntryChanges,
): Promise<AppliedPointEntryChanges> {
  const now = ctx.clock.now();
  const operations: AnyBulkWriteOperation<PointEntryDoc>[] = [
    ...changes.inserts.map(({ key, kind, fields }) => ({
      insertOne: {
        document: { _id: new ObjectId(), key, kind, ...fields, periodStart: fields.periodStart ?? null, source: 'backfill' as const, createdAt: now, updatedAt: now },
      },
    })),
    // Compare-and-set: an entry that a live sync changed after it was read is left alone; the next run sees it again.
    ...changes.updates.map(({ current, fields }) => ({
      updateOne: { filter: sameAsRead(current), update: { $set: { ...fields, source: 'recompute' as const, updatedAt: now } } },
    })),
    ...changes.deletes.map((current) => ({ deleteOne: { filter: sameAsRead(current) } })),
  ];
  if (operations.length === 0) return { created: 0, updated: 0, removed: 0 };
  try {
    const result = await pointEntriesCollection(ctx.db).bulkWrite(operations, { ordered: false });
    return { created: result.insertedCount, updated: result.modifiedCount, removed: result.deletedCount };
  } catch (err) {
    if (!(err instanceof MongoBulkWriteError)) throw err;
    const writeErrors = Array.isArray(err.writeErrors) ? err.writeErrors : [err.writeErrors];
    if (writeErrors.some((e) => e.code !== 11000)) throw err;
    return { created: err.result.insertedCount, updated: err.result.modifiedCount, removed: err.result.deletedCount };
  }
}

/** Entries of one person in `[from, to)`, newest date first, then by id. */
export function findPointEntriesInRange(db: Db, personId: ObjectId, from: Date, to: Date): Promise<PointEntryDoc[]> {
  return pointEntriesCollection(db)
    .find({ personId, date: { $gte: from, $lt: to } })
    .sort({ date: -1, _id: 1 })
    .toArray();
}

export interface PointTotal {
  personId: ObjectId;
  points: number;
  /** Entries of kind execution. */
  executions: number;
  /** Sum of the week and cycle bonus entries; part of `points`. */
  bonusPoints: number;
}

/** Sum and counts of the entries per person; `range` bounds are `[from, to)` and both optional. */
export function sumPointEntries(db: Db, range: { from?: Date; to?: Date }): Promise<PointTotal[]> {
  const date = { ...(range.from ? { $gte: range.from } : {}), ...(range.to ? { $lt: range.to } : {}) };
  return pointEntriesCollection(db)
    .aggregate<PointTotal>([
      { $match: Object.keys(date).length > 0 ? { date } : {} },
      {
        $group: {
          _id: '$personId',
          points: { $sum: '$amount' },
          executions: { $sum: { $cond: [{ $eq: ['$kind', 'execution'] }, 1, 0] } },
          bonusPoints: { $sum: { $cond: [{ $in: ['$kind', [...BONUS_KINDS]] }, '$amount', 0] } },
        },
      },
      { $project: { _id: 0, personId: '$_id', points: 1, executions: 1, bonusPoints: 1 } },
    ])
    .toArray();
}

/**
 * Removes the derived entries (executions and bonuses, never another kind), or only those dated
 * before `before`. A bonus is dated on the last day of its period, so a purge removes the bonuses
 * of the periods that ended before the boundary. Returns the number removed.
 */
export async function deleteDerivedPointEntries(db: Db, before?: Date): Promise<number> {
  const result = await pointEntriesCollection(db).deleteMany({
    kind: { $in: DERIVED_KINDS },
    ...(before ? { date: { $lt: before } } : {}),
  });
  return result.deletedCount;
}

/** Removes the whole ledger and returns how many entries went; an import rebuilds it from the imported occurrences. */
export async function clearPointEntries(db: Db): Promise<number> {
  return (await pointEntriesCollection(db).deleteMany({})).deletedCount;
}

export interface BonusEntryChanges {
  inserts: { key: string; kind: PointEntryKind; fields: PointEntryFields }[];
  deletes: PointEntryDoc[];
}

/**
 * Applies the bonus differences of a reconciliation as one ordered bulk write, deletes first
 * (ADR-0012). A bonus entry is only inserted or deleted, never updated: person and period are part
 * of its key. Deletes are compare-and-set on the entry that was read. No per-entry audit entry: the
 * caller records one summary. Inserted entries get source 'recompute'.
 */
export async function applyBonusEntryChanges(
  ctx: AuditContext,
  changes: BonusEntryChanges,
): Promise<{ created: number; removed: number }> {
  const now = ctx.clock.now();
  const operations: AnyBulkWriteOperation<PointEntryDoc>[] = [
    ...changes.deletes.map((current) => ({ deleteOne: { filter: { ...sameAsRead(current), key: current.key } } })),
    ...changes.inserts.map(({ key, kind, fields }) => ({
      insertOne: {
        document: {
          _id: new ObjectId(),
          key,
          kind,
          ...fields,
          periodStart: fields.periodStart ?? null,
          source: 'recompute' as const,
          createdAt: now,
          updatedAt: now,
        },
      },
    })),
  ];
  if (operations.length === 0) return { created: 0, removed: 0 };
  try {
    const result = await pointEntriesCollection(ctx.db).bulkWrite(operations, { ordered: true });
    return { created: result.insertedCount, removed: result.deletedCount };
  } catch (err) {
    // Only this reconciliation writes bonuses, so a duplicate key means an earlier run of it already wrote the entry.
    if (!(err instanceof MongoBulkWriteError)) throw err;
    const writeErrors = Array.isArray(err.writeErrors) ? err.writeErrors : [err.writeErrors];
    if (writeErrors.some((e) => e.code !== 11000)) throw err;
    return { created: err.result.insertedCount, removed: err.result.deletedCount };
  }
}
