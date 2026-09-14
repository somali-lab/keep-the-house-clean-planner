import { listRooms, type RoomDoc } from '../../src/data/rooms.ts';
import { listUsers, type UserDoc } from '../../src/data/users.ts';
import type { TestApp } from './testApp.ts';

/** Headers of the web client acting as the given profile. */
export function asProfile(user: { _id: { toHexString(): string } }, client: 'web' | 'api' = 'web') {
  return {
    'x-profile-id': user._id.toHexString(),
    ...(client === 'web' ? { 'x-client': 'web' } : {}),
  };
}

/** Seeded users in creation order (Persoon 1, Persoon 2). */
export async function seededUsers(t: TestApp): Promise<[UserDoc, UserDoc]> {
  const users = await listUsers(t.db);
  if (users.length < 2) throw new Error('expected at least two seeded users');
  return [users[0]!, users[1]!];
}

export async function seededRoom(t: TestApp, name = 'Badkamer'): Promise<RoomDoc> {
  const room = (await listRooms(t.db)).find((r) => r.name === name);
  if (!room) throw new Error(`seeded room not found: ${name}`);
  return room;
}
