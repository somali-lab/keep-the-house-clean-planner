import { MongoServerError, ObjectId, type Db } from 'mongodb';
import type { PointEntryKind, PointEntrySource, PointsSyncReason } from '@huishoudplanner/shared';
import type { AuditContext } from '../audit/context.ts';
import { diffFields, isEmptyDiff } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { COLLECTIONS } from './db.ts';

/** One entry of the points ledger (ADR-0011). Entries of kind 'execution' are derived from one occurrence. */
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
>;

const AUDIT_IGNORE = ['_id', 'createdAt', 'updatedAt'];

export const pointEntriesCollection = (db: Db) => db.collection<PointEntryDoc>(COLLECTIONS.pointEntries);

export const executionKey = (occurrenceId: ObjectId): string => `execution:${occurrenceId.toHexString()}`;

export function findPointEntryByKey(db: Db, key: string): Promise<PointEntryDoc | null> {
  return pointEntriesCollection(db).findOne({ key });
}

export function findPointEntries(db: Db, filter: Partial<Pick<PointEntryDoc, 'personId' | 'kind'>> = {}): Promise<PointEntryDoc[]> {
  return pointEntriesCollection(db).find(filter).sort({ date: -1, _id: 1 }).toArray();
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
  const doc: PointEntryDoc = { _id: new ObjectId(), key, kind, ...fields, source, createdAt: now, updatedAt: now };
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
  const diff = diffFields({ ...current }, { ...current, ...fields }, { ignore: [...AUDIT_IGNORE, 'source'] });
  if (isEmptyDiff(diff)) return false;
  const after = await pointEntriesCollection(ctx.db).findOneAndUpdate(
    { _id: current._id },
    { $set: { ...fields, source, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return false;
  await record(ctx, { entity: 'points', entityId: current._id, action: 'update', ...diff, meta: { ...meta } });
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
