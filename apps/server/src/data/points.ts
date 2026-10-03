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
  /** Redemptions only: free text, null when none (requirements 4.12). */
  note?: string | null;
  /** Redemptions only: cents one point was worth when it was booked (requirements 4.12). */
  centsPerPointSnapshot?: number | null;
  /** Redemptions only: the household currency when it was booked (requirements 4.12); missing on a booking from before it was kept. */
  currencyCodeSnapshot?: string | null;
  /** Redemptions only: idempotency key of the booking request (requirements 4.12). */
  requestId?: string | null;
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

// The request key is bookkeeping for retries, not history: it never shows in an audit entry (requirements 4.12).
const AUDIT_IGNORE = ['_id', 'createdAt', 'updatedAt', 'requestId'];

export const pointEntriesCollection = (db: Db) => db.collection<PointEntryDoc>(COLLECTIONS.pointEntries);

export const executionKey = (occurrenceId: ObjectId): string => `execution:${occurrenceId.toHexString()}`;

/** Kinds that are derived from the occurrences; a reconciliation manages these and never a booked kind (ADR-0011). */
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
  /** Points given up in redemptions, as a positive number; `points` already has them subtracted. */
  redeemed: number;
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
          redeemed: { $sum: { $cond: [{ $eq: ['$kind', 'redemption'] }, { $multiply: ['$amount', -1] }, 0] } },
        },
      },
      { $project: { _id: 0, personId: '$_id', points: 1, executions: 1, bonusPoints: 1, redeemed: 1 } },
    ])
    .toArray();
}

/**
 * The points a person earned in `[from, to)`: executions and bonuses, never a redemption, so spending points
 * does not lower the progress of the reward meter (requirements 4.12).
 */
export async function sumEarnedPoints(db: Db, personId: ObjectId, from: Date, to: Date): Promise<number> {
  const [total] = await pointEntriesCollection(db)
    .aggregate<{ points: number }>([
      { $match: { personId, kind: { $in: DERIVED_KINDS }, date: { $gte: from, $lt: to } } },
      { $group: { _id: null, points: { $sum: '$amount' } } },
    ])
    .toArray();
  return total?.points ?? 0;
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

export interface AppliedBonusEntryChanges {
  /** Keys of the entries that were really inserted; a duplicate key (already written) is not among them. */
  created: string[];
  /** Keys of the entries that are really gone; a delete that missed its compare-and-set is not among them. */
  removed: string[];
  /** The failure that stopped the write, if any; `created` and `removed` still say what did happen. */
  error?: unknown;
}

/**
 * Applies the bonus differences of a reconciliation (ADR-0012): the deletes first as one ordered
 * bulk write, then the inserts as one unordered write. A bonus entry is only inserted or deleted,
 * never updated: person and period are part of its key. Deletes are compare-and-set on the entry
 * that was read. No per-entry audit entry: the caller records one summary. Inserted entries get
 * source 'recompute'. What was done is read back, so the summary only lists writes that happened:
 * a delete that missed its compare-and-set and an insert that hit a duplicate key are left out. A
 * failure is returned, not thrown, so the caller can still record what was written before it.
 */
export async function applyBonusEntryChanges(ctx: AuditContext, changes: BonusEntryChanges): Promise<AppliedBonusEntryChanges> {
  const collection = pointEntriesCollection(ctx.db);
  const now = ctx.clock.now();
  const documents = changes.inserts.map(({ key, kind, fields }): PointEntryDoc => ({
    _id: new ObjectId(),
    key,
    kind,
    ...fields,
    periodStart: fields.periodStart ?? null,
    source: 'recompute',
    createdAt: now,
    updatedAt: now,
  }));
  let error: unknown;
  try {
    if (changes.deletes.length > 0) {
      await collection.bulkWrite(
        changes.deletes.map((current) => ({ deleteOne: { filter: { ...sameAsRead(current), key: current.key } } })),
        { ordered: true },
      );
    }
    if (documents.length > 0) {
      try {
        await collection.insertMany(documents, { ordered: false });
      } catch (err) {
        // Only this reconciliation writes bonuses, so a duplicate key means an earlier run already wrote the entry.
        if (!(err instanceof MongoBulkWriteError)) throw err;
        const writeErrors = Array.isArray(err.writeErrors) ? err.writeErrors : [err.writeErrors];
        if (writeErrors.some((e) => e.code !== 11000)) throw err;
      }
    }
  } catch (err) {
    error = err;
  }
  const ids = [...changes.deletes.map((doc) => doc._id), ...documents.map((doc) => doc._id)];
  const present = new Set(
    ids.length === 0 ? [] : (await collection.find({ _id: { $in: ids } }, { projection: { _id: 1 } }).toArray()).map((doc) => doc._id.toHexString()),
  );
  return {
    removed: changes.deletes.filter((doc) => !present.has(doc._id.toHexString())).map((doc) => doc.key),
    created: documents.filter((doc) => present.has(doc._id.toHexString())).map((doc) => doc.key),
    ...(error === undefined ? {} : { error }),
  };
}

/** A redemption: a booked ledger entry of a person giving up points (ADR-0011). Never derived, never touched by a reconciliation. */
export type RedemptionDoc = PointEntryDoc & {
  kind: 'redemption';
  occurrenceId: null;
  taskId: null;
  note: string | null;
  centsPerPointSnapshot: number;
  currencyCodeSnapshot: string;
  requestId: string | null;
};

/** The number of redemptions in the ledger; an import of an older file removes them (requirements 4.12). */
export function countRedemptions(db: Db): Promise<number> {
  return pointEntriesCollection(db).countDocuments({ kind: 'redemption' });
}

export const redemptionKey = (id: ObjectId): string => `redemption:${id.toHexString()}`;

export function findRedemptionById(db: Db, id: ObjectId): Promise<RedemptionDoc | null> {
  return pointEntriesCollection(db).findOne({ _id: id, kind: 'redemption' }) as Promise<RedemptionDoc | null>;
}

export function findRedemptionByRequestId(db: Db, requestId: string): Promise<RedemptionDoc | null> {
  return pointEntriesCollection(db).findOne({ requestId, kind: 'redemption' }) as Promise<RedemptionDoc | null>;
}

/** The balance of one person over the whole ledger: the sum of every entry, redemptions included. */
export async function sumPersonBalance(db: Db, personId: ObjectId): Promise<number> {
  const [total] = await pointEntriesCollection(db)
    .aggregate<{ points: number }>([{ $match: { personId } }, { $group: { _id: null, points: { $sum: '$amount' } } }])
    .toArray();
  return total?.points ?? 0;
}

/**
 * Inserts a booked redemption and audits it. A duplicate request key (a concurrent booking of the
 * same request won) is reported as `inserted: false`, writing and auditing nothing, so the caller can
 * decide between replaying and conflicting.
 */
export async function insertRedemption(
  ctx: AuditContext,
  doc: RedemptionDoc,
): Promise<{ inserted: true; doc: RedemptionDoc } | { inserted: false }> {
  try {
    await pointEntriesCollection(ctx.db).insertOne(doc);
  } catch (err) {
    if (err instanceof MongoServerError && err.code === 11000) return { inserted: false };
    throw err;
  }
  const { after } = diffFields({}, { ...doc }, { ignore: AUDIT_IGNORE });
  await record(ctx, { entity: 'points', entityId: doc._id, action: 'create', after, meta: { reason: 'redemption' } });
  return { inserted: true, doc };
}

/** Removes a redemption and audits it. Returns null, writing and auditing nothing, when it is already gone. */
export async function deleteRedemption(ctx: AuditContext, id: ObjectId): Promise<RedemptionDoc | null> {
  const deleted = (await pointEntriesCollection(ctx.db).findOneAndDelete({ _id: id, kind: 'redemption' })) as RedemptionDoc | null;
  if (!deleted) return null;
  const { before } = diffFields({ ...deleted }, {}, { ignore: AUDIT_IGNORE });
  await record(ctx, { entity: 'points', entityId: deleted._id, action: 'delete', before, meta: { reason: 'redemption_undone' } });
  return deleted;
}

/**
 * Removes the redemptions, or only those dated before `before` (the statistics reset, requirements 4.12). The
 * caller records them in its single reset audit entry. Returns the number removed.
 */
export async function deleteRedemptions(db: Db, before?: Date): Promise<number> {
  const result = await pointEntriesCollection(db).deleteMany({ kind: 'redemption', ...(before ? { date: { $lt: before } } : {}) });
  return result.deletedCount;
}

/** The on-time week bonuses of the given people (everybody when null), dated on the last day of their week; badges count these (ADR-0014). */
export function findWeekOnTimeBonuses(db: Db, personIds: ObjectId[] | null): Promise<{ personId: ObjectId; date: Date }[]> {
  return pointEntriesCollection(db)
    .find(
      { kind: 'bonus_week_ontime', ...(personIds ? { personId: { $in: personIds } } : {}) },
      { projection: { personId: 1, date: 1 } },
    )
    .sort({ date: 1, _id: 1 })
    .toArray() as Promise<{ personId: ObjectId; date: Date }[]>;
}
