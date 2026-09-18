import type { AuditAction, OccurrenceStatus } from '@huishoudplanner/shared';
import { MongoBulkWriteError, ObjectId, type Db, type Filter } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { diffFields, isEmptyDiff } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { COLLECTIONS } from './db.ts';

export interface OccurrenceDoc {
  _id: ObjectId;
  taskId: ObjectId;
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
  createdAt: Date;
  updatedAt: Date;
}

const AUDIT_IGNORE = ['_id', 'createdAt', 'updatedAt'];

export const occurrencesCollection = (db: Db) => db.collection<OccurrenceDoc>(COLLECTIONS.occurrences);

export function findOccurrenceById(db: Db, id: ObjectId): Promise<OccurrenceDoc | null> {
  return occurrencesCollection(db).findOne({ _id: id });
}

export function findOccurrences(db: Db, filter: Filter<OccurrenceDoc>): Promise<OccurrenceDoc[]> {
  return occurrencesCollection(db).find(filter).sort({ date: 1, taskNameSnapshot: 1, _id: 1 }).toArray();
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
  const taskIds = [...new Map(docs.map((doc) => [doc.taskId.toHexString(), doc.taskId])).values()];
  const tasks = await db.collection<{ _id: ObjectId; roomId: ObjectId }>(COLLECTIONS.tasks).find({ _id: { $in: taskIds } }).toArray();
  const roomIds = [...new Map(tasks.map((task) => [task.roomId.toHexString(), task.roomId])).values()];
  const rooms = await db.collection<{ _id: ObjectId; name: string }>(COLLECTIONS.rooms).find({ _id: { $in: roomIds } }).toArray();
  const taskRoom = new Map(tasks.map((task) => [task._id.toHexString(), task.roomId]));
  const roomName = new Map(rooms.map((room) => [room._id.toHexString(), room.name]));
  const result = await occurrencesCollection(db).bulkWrite(
    docs.map((doc) => {
      const roomId = taskRoom.get(doc.taskId.toHexString()) ?? null;
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
 * Idempotent bulk insert keyed on the unique index (cycleId, taskId, plannedDate):
 * duplicates are ignored, and only documents that were actually inserted are audited.
 */
export async function insertOccurrencesIdempotent(
  ctx: AuditContext,
  docs: OccurrenceDoc[],
  meta: Record<string, unknown>,
): Promise<OccurrenceDoc[]> {
  if (docs.length === 0) return [];
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
