import type { CreateUserInput, UpdateUserInput, UserRole } from '@huishoudplanner/shared';
import { ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { diffFields, isEmptyDiff } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { HttpError } from '../http/errors.ts';
import { COLLECTIONS } from './db.ts';

export interface UserDoc {
  _id: ObjectId;
  name: string;
  color: string;
  active: boolean;
  role: UserRole;
  unavailableWeekdays: number[];
  dailyBudgetMinutes: { weekday: number; weekend: number };
  maxDailyMinutes: { weekday: number; weekend: number };
  createdAt: Date;
  updatedAt: Date;
}

const CREATE_IGNORE = ['_id', 'createdAt', 'updatedAt'];

const usersCollection = (db: Db) => db.collection<UserDoc>(COLLECTIONS.users);

const withDefaults = (user: UserDoc): UserDoc => ({
  ...user,
  // Existing installations predate roles. Preserve access until an admin assigns explicit roles.
  role: user.role ?? 'admin',
  maxDailyMinutes: user.maxDailyMinutes ?? { weekday: 480, weekend: 480 },
});

export async function findUserById(db: Db, id: ObjectId): Promise<UserDoc | null> {
  const user = await usersCollection(db).findOne({ _id: id });
  return user ? withDefaults(user) : null;
}

export async function listUsers(db: Db, filter: { active?: boolean } = {}): Promise<UserDoc[]> {
  const query = filter.active === undefined ? {} : { active: filter.active };
  return (await usersCollection(db).find(query).sort({ createdAt: 1, _id: 1 }).toArray()).map(withDefaults);
}

export function countUsers(db: Db): Promise<number> {
  return usersCollection(db).countDocuments();
}

export async function createUser(
  ctx: AuditContext,
  input: Omit<CreateUserInput, 'maxDailyMinutes' | 'role'> & Partial<Pick<CreateUserInput, 'maxDailyMinutes' | 'role'>>,
): Promise<UserDoc> {
  const now = ctx.clock.now();
  const doc: UserDoc = {
    _id: new ObjectId(),
    name: input.name,
    color: input.color,
    active: true,
    role: input.role ?? 'member',
    unavailableWeekdays: [...new Set(input.unavailableWeekdays)].sort(),
    dailyBudgetMinutes: input.dailyBudgetMinutes,
    maxDailyMinutes: input.maxDailyMinutes ?? { weekday: 60, weekend: 120 },
    createdAt: now,
    updatedAt: now,
  };
  await usersCollection(ctx.db).insertOne(doc);
  const { after } = diffFields({}, { ...doc }, { ignore: CREATE_IGNORE });
  await record(ctx, { entity: 'user', entityId: doc._id, action: 'create', after });
  return doc;
}

/** Returns null if the user does not exist; skips the write (and audit) when nothing changes. */
export async function updateUser(
  ctx: AuditContext,
  id: ObjectId,
  patch: UpdateUserInput,
): Promise<UserDoc | null> {
  const before = await findUserById(ctx.db, id);
  if (!before) return null;

  const removesLastAdmin =
    before.active &&
    before.role === 'admin' &&
    (patch.active === false || (patch.role !== undefined && patch.role !== 'admin'));
  if (removesLastAdmin) {
    const otherAdmins = await usersCollection(ctx.db).countDocuments({
      _id: { $ne: id },
      active: true,
      $or: [{ role: 'admin' }, { role: { $exists: false } }],
    });
    if (otherAdmins === 0) throw new HttpError(409, 'last_admin', 'At least one active administrator is required');
  }

  const changes: Partial<UserDoc> = { ...patch };
  if (patch.unavailableWeekdays) {
    changes.unavailableWeekdays = [...new Set(patch.unavailableWeekdays)].sort();
  }
  const diff = diffFields({ ...before }, { ...before, ...changes });
  if (isEmptyDiff(diff)) return before;

  const after = await usersCollection(ctx.db).findOneAndUpdate(
    { _id: id },
    { $set: { ...changes, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return null;
  await record(ctx, { entity: 'user', entityId: id, action: 'update', ...diff });
  return after;
}
