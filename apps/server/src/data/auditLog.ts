import type { AuditAction, AuditEntity, AuditSource } from '@huishoudplanner/shared';
import { ObjectId, type Db, type Filter } from 'mongodb';
import { COLLECTIONS } from './db.ts';

export interface AuditEntryDoc {
  _id: ObjectId;
  at: Date;
  actorId: ObjectId;
  entity: AuditEntity;
  entityId: ObjectId;
  action: AuditAction;
  before: Record<string, unknown>;
  after: Record<string, unknown>;
  source: AuditSource;
  meta?: Record<string, unknown>;
}

export const auditLogCollection = (db: Db) => db.collection<AuditEntryDoc>(COLLECTIONS.auditLog);

export interface AuditQuery {
  entity?: AuditEntity;
  entityId?: ObjectId;
  actorId?: ObjectId;
  source?: AuditSource;
  from?: Date;
  to?: Date;
  /** Keyset position: entries strictly after this one in (at desc, _id desc) order. */
  after?: { at: Date; id: ObjectId };
  limit: number;
}

/** Newest first; ties on `at` are ordered by `_id` so cursors are stable. */
export function findAuditEntries(db: Db, query: AuditQuery): Promise<AuditEntryDoc[]> {
  const filter: Filter<AuditEntryDoc> = {
    ...(query.entity ? { entity: query.entity } : {}),
    ...(query.entityId ? { entityId: query.entityId } : {}),
    ...(query.actorId ? { actorId: query.actorId } : {}),
    ...(query.source ? { source: query.source } : {}),
  };
  if (query.from || query.to) {
    filter.at = { ...(query.from ? { $gte: query.from } : {}), ...(query.to ? { $lte: query.to } : {}) };
  }
  if (query.after) {
    filter.$or = [{ at: { $lt: query.after.at } }, { at: query.after.at, _id: { $lt: query.after.id } }];
  }
  return auditLogCollection(db).find(filter).sort({ at: -1, _id: -1 }).limit(query.limit).toArray();
}

/** Append-only: use only via audit/record(). No update or delete exists for this collection. */
export async function insertAuditEntry(db: Db, entry: Omit<AuditEntryDoc, '_id'>): Promise<ObjectId> {
  const doc: AuditEntryDoc = { _id: new ObjectId(), ...entry };
  await auditLogCollection(db).insertOne(doc);
  return doc._id;
}

/** Explicit user-requested reset of the complete activity log. */
export async function clearAuditEntries(db: Db): Promise<number> {
  const result = await auditLogCollection(db).deleteMany({});
  return result.deletedCount;
}
