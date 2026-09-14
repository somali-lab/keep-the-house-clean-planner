import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { UserDoc } from '../src/data/users.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;
let p1: UserDoc;

beforeAll(async () => {
  t = await createTestApp();
  [p1] = await seededUsers(t);
});

afterAll(async () => {
  await t.close();
});

describe('rooms API', () => {
  it('lists seeded rooms in sort order, including the virtual room', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/api/rooms' });
    expect(res.statusCode).toBe(200);
    const rooms = res.json<{ name: string; virtual: boolean }[]>();
    expect(rooms.map((r) => r.name)).toEqual([
      'Keuken',
      'Badkamer',
      'Toilet',
      'Woonkamer',
      'Slaapkamer',
      'Hal',
      'Hele huis',
    ]);
    expect(rooms.at(-1)).toMatchObject({ name: 'Hele huis', virtual: true });
  });

  it('creates a room at the end of the list and audits it', async () => {
    const { result, entries } = await expectAudited(
      t,
      () => t.app.inject({ method: 'POST', url: '/api/rooms', headers: asProfile(p1), payload: { name: 'Zolder' } }),
      { entity: 'room', action: 'create', source: 'ui', count: 1 },
    );
    expect(result.statusCode).toBe(201);
    expect(result.json()).toMatchObject({ name: 'Zolder', sortOrder: 80, active: true, virtual: false });
    expect(entries[0]!.after).toEqual({ name: 'Zolder', sortOrder: 80, active: true, virtual: false });
  });

  it('validates input', async () => {
    const res = await t.app.inject({ method: 'POST', url: '/api/rooms', headers: asProfile(p1), payload: { name: '' } });
    expect(res.statusCode).toBe(400);
    expect(res.json<{ details: { field: string }[] }>().details.map((d) => d.field)).toContain('name');
  });

  it('renames and deactivates with audited before/after', async () => {
    const hal = await seededRoom(t, 'Hal');
    const { result, entries } = await expectAudited(
      t,
      () =>
        t.app.inject({
          method: 'PATCH',
          url: `/api/rooms/${hal._id.toHexString()}`,
          headers: asProfile(p1),
          payload: { name: 'Gang', active: false },
        }),
      { entity: 'room', action: 'update', count: 1 },
    );
    expect(result.statusCode).toBe(200);
    expect(entries[0]!.before).toEqual({ name: 'Hal', active: true });
    expect(entries[0]!.after).toEqual({ name: 'Gang', active: false });
    const active = await t.app.inject({ method: 'GET', url: '/api/rooms?active=true' });
    expect(active.json<{ name: string }[]>().map((r) => r.name)).not.toContain('Gang');
  });

  it('returns 404 for an unknown room', async () => {
    const res = await t.app.inject({
      method: 'PATCH',
      url: '/api/rooms/0123456789abcdef01234567',
      headers: asProfile(p1),
      payload: { name: 'X' },
    });
    expect(res.statusCode).toBe(404);
  });

  it('deletes an empty room and refuses while active or inactive tasks still use it', async () => {
    const emptyResponse = await t.app.inject({ method: 'POST', url: '/api/rooms', headers: asProfile(p1), payload: { name: 'Lege kamer' } });
    const empty = emptyResponse.json<{ _id: string }>();
    const removed = await expectAudited(
      t,
      () => t.app.inject({ method: 'DELETE', url: `/api/rooms/${empty._id}`, headers: asProfile(p1) }),
      { entity: 'room', action: 'delete', count: 1 },
    );
    expect(removed.result.json()).toEqual({ deleted: true });

    const usedResponse = await t.app.inject({ method: 'POST', url: '/api/rooms', headers: asProfile(p1), payload: { name: 'Gebruikte kamer' } });
    const used = usedResponse.json<{ _id: string }>();
    const task = await t.app.inject({
      method: 'POST', url: '/api/tasks', headers: asProfile(p1),
      payload: { name: 'Klus', roomId: used._id, intervalKey: '1w', durationMinutes: 5 },
    });
    await t.app.inject({ method: 'PATCH', url: `/api/tasks/${task.json<{ _id: string }>()._id}`, headers: asProfile(p1), payload: { active: false } });
    const blocked = await t.app.inject({ method: 'DELETE', url: `/api/rooms/${used._id}`, headers: asProfile(p1) });
    expect(blocked.statusCode).toBe(409);
    expect(blocked.json()).toMatchObject({ code: 'room_in_use', details: { taskCount: 1 } });
  });
});
