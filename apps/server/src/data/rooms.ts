import type { CreateRoomInput, UpdateRoomInput } from '@huishoudplanner/shared';
import { ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { diffFields, isEmptyDiff } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { COLLECTIONS } from './db.ts';

export interface RoomDoc {
  _id: ObjectId;
  name: string;
  sortOrder: number;
  active: boolean;
  virtual: boolean;
  createdAt: Date;
  updatedAt: Date;
}

const roomsCollection = (db: Db) => db.collection<RoomDoc>(COLLECTIONS.rooms);

export function findRoomById(db: Db, id: ObjectId): Promise<RoomDoc | null> {
  return roomsCollection(db).findOne({ _id: id });
}

export function listRooms(db: Db, filter: { active?: boolean } = {}): Promise<RoomDoc[]> {
  const query = filter.active === undefined ? {} : { active: filter.active };
  return roomsCollection(db).find(query).sort({ sortOrder: 1, name: 1 }).toArray();
}

export function countRooms(db: Db): Promise<number> {
  return roomsCollection(db).countDocuments();
}

async function nextSortOrder(db: Db): Promise<number> {
  const [last] = await roomsCollection(db).find({}).sort({ sortOrder: -1 }).limit(1).toArray();
  return last ? last.sortOrder + 10 : 10;
}

export async function createRoom(ctx: AuditContext, input: CreateRoomInput): Promise<RoomDoc> {
  const now = ctx.clock.now();
  const doc: RoomDoc = {
    _id: new ObjectId(),
    name: input.name,
    sortOrder: input.sortOrder ?? (await nextSortOrder(ctx.db)),
    active: true,
    virtual: input.virtual,
    createdAt: now,
    updatedAt: now,
  };
  await roomsCollection(ctx.db).insertOne(doc);
  const { after } = diffFields({}, { ...doc }, { ignore: ['_id', 'createdAt', 'updatedAt'] });
  await record(ctx, { entity: 'room', entityId: doc._id, action: 'create', after });
  return doc;
}

export async function deleteRoom(ctx: AuditContext, id: ObjectId): Promise<RoomDoc | null> {
  const before = await findRoomById(ctx.db, id);
  if (!before) return null;
  const result = await roomsCollection(ctx.db).deleteOne({ _id: id });
  if (result.deletedCount !== 1) return null;
  const { before: auditBefore } = diffFields(
    { ...before },
    {},
    { ignore: ['_id', 'createdAt', 'updatedAt'] },
  );
  await record(ctx, { entity: 'room', entityId: id, action: 'delete', before: auditBefore });
  return before;
}

/** Returns null if the room does not exist; skips the write (and audit) when nothing changes. */
export async function updateRoom(
  ctx: AuditContext,
  id: ObjectId,
  patch: UpdateRoomInput,
): Promise<RoomDoc | null> {
  const before = await findRoomById(ctx.db, id);
  if (!before) return null;
  const diff = diffFields({ ...before }, { ...before, ...patch });
  if (isEmptyDiff(diff)) return before;

  const after = await roomsCollection(ctx.db).findOneAndUpdate(
    { _id: id },
    { $set: { ...patch, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return null;
  await record(ctx, { entity: 'room', entityId: id, action: 'update', ...diff });
  return after;
}
