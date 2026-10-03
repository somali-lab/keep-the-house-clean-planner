import type { AuditAction, OccurrenceStatus } from '@huishoudplanner/shared';
import { MongoBulkWriteError, MongoServerError, ObjectId, type Db, type Filter } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { diffFields, isEmptyDiff } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { COLLECTIONS } from './db.ts';

export interface OccurrenceDoc {
  _id: ObjectId;
  /** Null for a one-off task (ADR-0009): name, duration and room live in the snapshot fields only. */
  taskId: ObjectId | null;
  cycleId: ObjectId;
  /** Plan the occurrence was generated from; null for ad-hoc occurrences. */
  planId: ObjectId | null;
  /** 00:00 Europe/Amsterdam of the day it currently sits on. */
  date: Date;
  /** 00:00 Europe/Amsterdam of the original slot day; kept when dragged. */
  plannedDate: Date;
  assigneeId: ObjectId | null;
  status: OccurrenceStatus;
  statusBeforeCompletion: 'open' | 'skipped' | null;
  completedAt: Date | null;
  completedBy: ObjectId | null;
  skipReason: string | null;
  durationMinutesSnapshot: number;
  taskNameSnapshot: string;
  /** Room at the time this occurrence was planned; optional on pre-migration data. */
  roomIdSnapshot?: ObjectId | null;
  roomNameSnapshot?: string | null;
  origin: 'generated' | 'adhoc';
  /** Created directly in the done state (no planned state to return to); missing on older data means false. */
  recordedDone?: boolean;
  /** Client idempotency key of an ad-hoc creation; missing on older data means null. */
  requestId?: string | null;
  createdAt: Date;
  updatedAt: Date;
}

const AUDIT_IGNORE = ['_id', 'createdAt', 'updatedAt'];

export const occurrencesCollection = (db: Db) => db.collection<OccurrenceDoc>(COLLECTIONS.occurrences);

export function findOccurrenceById(db: Db, id: ObjectId): Promise<OccurrenceDoc | null> {
  return occurrencesCollection(db).findOne({ _id: id });
}

export function findOccurrenceByRequestId(db: Db, requestId: string): Promise<OccurrenceDoc | null> {
  return occurrencesCollection(db).findOne({ requestId });
}

export function findOccurrences(db: Db, filter: Filter<OccurrenceDoc>): Promise<OccurrenceDoc[]> {
  return occurrencesCollection(db).find(filter).sort({ date: 1, taskNameSnapshot: 1, _id: 1 }).toArray();
}

export function findFirstGeneratedPlannedDates(
  db: Db,
): Promise<{ taskId: ObjectId; plannedDate: Date }[]> {
  return occurrencesCollection(db)
    .aggregate<{ taskId: ObjectId; plannedDate: Date }>([
      { $match: { origin: 'generated' } },
      { $group: { _id: '$taskId', plannedDate: { $min: '$plannedDate' } } },
      { $project: { _id: 0, taskId: '$_id', plannedDate: 1 } },
    ])
    .toArray();
}

export function countOccurrences(db: Db, filter: Filter<OccurrenceDoc> = {}): Promise<number> {
  return occurrencesCollection(db).countDocuments(filter);
}

/** Backfills room snapshots on occurrences created before room history was introduced. */
export async function backfillOccurrenceRoomSnapshots(db: Db): Promise<number> {
  const docs = await occurrencesCollection(db)
    .find({
      $or: [
        { roomIdSnapshot: { $exists: false } },
        { roomNameSnapshot: { $exists: false } },
      ],
    })
    .toArray();
  if (docs.length === 0) return 0;
  // A one-off task (taskId null) always has its snapshots written and is never matched here.
  const taskIds = [...new Map(docs.flatMap((doc) => (doc.taskId ? [[doc.taskId.toHexString(), doc.taskId] as const] : []))).values()];
  const tasks = await db.collection<{ _id: ObjectId; roomId: ObjectId }>(COLLECTIONS.tasks).find({ _id: { $in: taskIds } }).toArray();
  const roomIds = [...new Map(tasks.map((task) => [task.roomId.toHexString(), task.roomId])).values()];
  const rooms = await db.collection<{ _id: ObjectId; name: string }>(COLLECTIONS.rooms).find({ _id: { $in: roomIds } }).toArray();
  const taskRoom = new Map(tasks.map((task) => [task._id.toHexString(), task.roomId]));
  const roomName = new Map(rooms.map((room) => [room._id.toHexString(), room.name]));
  const result = await occurrencesCollection(db).bulkWrite(
    docs.map((doc) => {
      const roomId = (doc.taskId ? taskRoom.get(doc.taskId.toHexString()) : null) ?? null;
      return {
        updateOne: {
          filter: { _id: doc._id },
          update: {
            $set: {
              roomIdSnapshot: roomId,
              roomNameSnapshot: roomId ? (roomName.get(roomId.toHexString()) ?? null) : null,
            },
          },
        },
      };
    }),
  );
  return result.modifiedCount;
}

/** A room move follows future open work while completed history keeps its snapshot. */
export async function updateUpcomingOccurrenceRoomSnapshots(
  db: Db,
  taskId: ObjectId,
  from: Date,
  roomId: ObjectId,
  roomName: string,
): Promise<number> {
  const result = await occurrencesCollection(db).updateMany(
    { taskId, status: 'open', date: { $gte: from } },
    { $set: { roomIdSnapshot: roomId, roomNameSnapshot: roomName } },
  );
  return result.modifiedCount;
}

/**
 * Idempotent bulk insert of generated occurrences, keyed on the partial unique
 * index (cycleId, taskId, plannedDate) for origin 'generated': duplicates are
 * ignored, and only documents that were actually inserted are audited.
 */
export async function insertOccurrencesIdempotent(
  ctx: AuditContext,
  docs: OccurrenceDoc[],
  meta: Record<string, unknown>,
): Promise<OccurrenceDoc[]> {
  if (docs.length === 0) return [];
  if (docs.some((doc) => doc.origin !== 'generated')) {
    throw new Error('insertOccurrencesIdempotent accepts generated occurrences only');
  }
  let failed = new Set<number>();
  try {
    await occurrencesCollection(ctx.db).insertMany(docs, { ordered: false });
  } catch (err) {
    if (!(err instanceof MongoBulkWriteError)) throw err;
    const writeErrors = Array.isArray(err.writeErrors) ? err.writeErrors : [err.writeErrors];
    if (writeErrors.some((e) => e.code !== 11000)) throw err;
    failed = new Set(writeErrors.map((e) => e.index));
  }
  const inserted = docs.filter((_, i) => !failed.has(i));
  for (const doc of inserted) {
    const { after } = diffFields({}, { ...doc }, { ignore: AUDIT_IGNORE });
    await record(ctx, { entity: 'occurrence', entityId: doc._id, action: 'create', after, meta });
  }
  return inserted;
}

/**
 * Inserts one ad-hoc occurrence and audits it. A duplicate key (a repeated
 * requestId) is reported as `inserted: false` instead of being swallowed, so the
 * caller can decide between replaying and conflicting.
 */
export async function insertAdhocOccurrence(
  ctx: AuditContext,
  doc: OccurrenceDoc,
  meta: Record<string, unknown>,
): Promise<{ inserted: true; doc: OccurrenceDoc } | { inserted: false }> {
  if (doc.origin !== 'adhoc') throw new Error('insertAdhocOccurrence accepts ad-hoc occurrences only');
  try {
    await occurrencesCollection(ctx.db).insertOne(doc);
  } catch (err) {
    if (err instanceof MongoServerError && err.code === 11000) return { inserted: false };
    throw err;
  }
  const { after } = diffFields({}, { ...doc }, { ignore: AUDIT_IGNORE });
  await record(ctx, { entity: 'occurrence', entityId: doc._id, action: 'create', after, meta });
  return { inserted: true, doc };
}

/**
 * Atomically deletes recorded work (an ad-hoc occurrence created done) and audits it as
 * 'delete' with the retract reason. Returns null, writing and auditing nothing, when no such
 * occurrence exists (any more).
 */
export async function retractRecordedOccurrence(ctx: AuditContext, id: ObjectId): Promise<OccurrenceDoc | null> {
  const doc = await occurrencesCollection(ctx.db).findOneAndDelete({
    _id: id,
    origin: 'adhoc',
    recordedDone: true,
    status: 'done',
  });
  if (!doc) return null;
  const { before } = diffFields({ ...doc }, {}, { ignore: AUDIT_IGNORE });
  await record(ctx, { entity: 'occurrence', entityId: doc._id, action: 'delete', before, meta: { reason: 'retract' } });
  return doc;
}

/** Deletes the given occurrences, auditing each as action 'delete' with its previous fields. */
export async function deleteOccurrences(
  ctx: AuditContext,
  docs: OccurrenceDoc[],
  meta: Record<string, unknown>,
): Promise<number> {
  if (docs.length === 0) return 0;
  const result = await occurrencesCollection(ctx.db).deleteMany({ _id: { $in: docs.map((d) => d._id) } });
  for (const doc of docs) {
    const { before } = diffFields({ ...doc }, {}, { ignore: AUDIT_IGNORE });
    await record(ctx, { entity: 'occurrence', entityId: doc._id, action: 'delete', before, meta });
  }
  return result.deletedCount;
}

/**
 * Audited field update. `expect` narrows the filter for atomic transitions
 * (e.g. claim only while unassigned); returns null when nothing matched.
 */
export async function updateOccurrence(
  ctx: AuditContext,
  id: ObjectId,
  changes: Partial<Omit<OccurrenceDoc, '_id' | 'createdAt' | 'updatedAt'>>,
  audit: { action: AuditAction; meta?: Record<string, unknown> },
  expect: Filter<OccurrenceDoc> = {},
): Promise<{ before: OccurrenceDoc; after: OccurrenceDoc } | null> {
  const before = await occurrencesCollection(ctx.db).findOne({ ...expect, _id: id });
  if (!before) return null;
  const diff = diffFields({ ...before }, { ...before, ...changes });
  if (isEmptyDiff(diff)) return { before, after: before };

  const after = await occurrencesCollection(ctx.db).findOneAndUpdate(
    { ...expect, _id: id },
    { $set: { ...changes, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return null;
  const occurrence = {
    taskNameSnapshot: before.taskNameSnapshot,
    roomNameSnapshot: before.roomNameSnapshot ?? null,
    date: before.date,
  };
  await record(ctx, {
    entity: 'occurrence',
    entityId: id,
    action: audit.action,
    ...diff,
    meta: { ...audit.meta, occurrence },
  });
  return { before, after };
}
