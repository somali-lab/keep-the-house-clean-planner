import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { UserDoc } from '../src/data/users.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededUsers } from './helpers/http.ts';
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

describe('users API', () => {
  it('lists users as JSON with string ids', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/api/users' });
    expect(res.statusCode).toBe(200);
    const users = res.json<{ _id: string; name: string; createdAt: string }[]>();
    expect(users.map((u) => u.name)).toEqual(['Persoon 1', 'Persoon 2']);
    expect(users[0]!._id).toBe(p1._id.toHexString());
    expect(users[0]!.createdAt).toBe('2026-09-16T08:00:00.000Z');
  });

  it('creates a user (201) and audits the create with source ui', async () => {
    const { result, entries } = await expectAudited(
      t,
      () =>
        t.app.inject({
          method: 'POST',
          url: '/api/users',
          headers: asProfile(p1),
          payload: { name: 'Logé', color: '#16a34a', unavailableWeekdays: [2, 2, 0] },
        }),
      { entity: 'user', action: 'create', source: 'ui', count: 1 },
    );
    expect(result.statusCode).toBe(201);
    const body = result.json<{ _id: string; unavailableWeekdays: number[]; active: boolean }>();
    expect(body).toMatchObject({ unavailableWeekdays: [0, 2], active: true });
    expect(entries[0]!.entityId.toHexString()).toBe(body._id);
    expect(entries[0]!.actorId).toEqual(p1._id);
  });

  it('rejects invalid input with field names', async () => {
    const res = await t.app.inject({
      method: 'POST',
      url: '/api/users',
      headers: asProfile(p1),
      payload: { color: 'blue', unavailableWeekdays: [7] },
    });
    expect(res.statusCode).toBe(400);
    const fields = res.json<{ details: { field: string }[] }>().details.map((d) => d.field);
    expect(fields).toEqual(expect.arrayContaining(['name', 'color', 'unavailableWeekdays.0']));
  });

  it('requires a profile for writes', async () => {
    const res = await t.app.inject({ method: 'POST', url: '/api/users', payload: { name: 'X', color: '#000000' } });
    expect(res.statusCode).toBe(400);
    expect(res.json()).toMatchObject({ code: 'profile_required' });
  });

  it('updates availability and budgets; audit holds only changed fields', async () => {
    const [, p2] = await seededUsers(t);
    const { result, entries } = await expectAudited(
      t,
      () =>
        t.app.inject({
          method: 'PATCH',
          url: `/api/users/${p2._id.toHexString()}`,
          headers: asProfile(p1, 'api'),
          payload: { unavailableWeekdays: [2], dailyBudgetMinutes: { weekday: 45, weekend: 120 } },
        }),
      { entity: 'user', action: 'update', source: 'api', count: 1 },
    );
    expect(result.statusCode).toBe(200);
    expect(entries[0]!.before).toEqual({ unavailableWeekdays: [], dailyBudgetMinutes: { weekday: 60 } });
    expect(entries[0]!.after).toEqual({ unavailableWeekdays: [2], dailyBudgetMinutes: { weekday: 45 } });
  });

  it('deactivates instead of deleting, and filters by active', async () => {
    const created = await t.app.inject({
      method: 'POST',
      url: '/api/users',
      headers: asProfile(p1),
      payload: { name: 'Tijdelijk', color: '#000000' },
    });
    const id = created.json<{ _id: string }>()._id;
    await expectAudited(
      t,
      () => t.app.inject({ method: 'PATCH', url: `/api/users/${id}`, headers: asProfile(p1), payload: { active: false } }),
      { entity: 'user', action: 'update', count: 1 },
    );
    const active = await t.app.inject({ method: 'GET', url: '/api/users?active=true' });
    expect(active.json<{ _id: string }[]>().map((u) => u._id)).not.toContain(id);
    const all = await t.app.inject({ method: 'GET', url: '/api/users' });
    expect(all.json<{ _id: string }[]>().map((u) => u._id)).toContain(id);
    const del = await t.app.inject({ method: 'DELETE', url: `/api/users/${id}`, headers: asProfile(p1) });
    expect(del.statusCode).toBe(404);
  });

  it('returns 404 for unknown and 400 for malformed ids', async () => {
    const unknown = await t.app.inject({
      method: 'PATCH',
      url: '/api/users/0123456789abcdef01234567',
      headers: asProfile(p1),
      payload: { name: 'X' },
    });
    expect(unknown.statusCode).toBe(404);
    const malformed = await t.app.inject({
      method: 'PATCH',
      url: '/api/users/nope',
      headers: asProfile(p1),
      payload: { name: 'X' },
    });
    expect(malformed.statusCode).toBe(400);
  });
});
