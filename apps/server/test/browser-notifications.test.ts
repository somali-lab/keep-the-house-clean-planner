import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { UserDoc } from '../src/data/users.ts';
import { captureWrites, expectAudited } from './helpers/audit.ts';
import { asProfile, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;
let admin: UserDoc;
let member: UserDoc;

beforeAll(async () => {
  t = await createTestApp();
  [admin, member] = await seededUsers(t);
});

afterAll(async () => {
  await t.close();
});

const put = (actor: UserDoc, target: UserDoc, payload: unknown) =>
  t.app.inject({
    method: 'PUT',
    url: `/api/users/${target._id.toHexString()}/browser-notifications`,
    headers: asProfile(actor),
    payload: payload as Record<string, unknown>,
  });

const stored = async (user: UserDoc) =>
  (await t.app.inject({ method: 'GET', url: '/api/users' }))
    .json<{ _id: string; browserNotifications: { enabled: boolean; times: string[] } }[]>()
    .find((u) => u._id === user._id.toHexString())!.browserNotifications;

describe('browser notification moments', () => {
  it('reads as disabled without times for a user that never configured them', async () => {
    expect(await stored(member)).toEqual({ enabled: false, times: [] });
  });

  it('reads documents that predate the field as disabled without times', async () => {
    // Re-import the current export without the field, as a backup from before this feature would look.
    const file = (await t.app.inject({ method: 'GET', url: '/api/export/json' })).json<{
      collections: { users: Record<string, unknown>[] };
    }>();
    for (const user of file.collections.users) delete user.browserNotifications;
    const res = await t.app.inject({
      method: 'POST',
      url: '/api/import/json?mode=replace&confirm=true',
      headers: asProfile(admin),
      payload: file as unknown as Record<string, unknown>,
    });
    expect(res.statusCode, res.body).toBeLessThan(300);
    expect(await stored(admin)).toEqual({ enabled: false, times: [] });
    expect(await stored(member)).toEqual({ enabled: false, times: [] });
  });

  it('lets a person set their own moments, sorted, and audits the change', async () => {
    const { result, entries } = await expectAudited(
      t,
      () => put(member, member, { enabled: true, times: ['18:30', '08:00'] }),
      { entity: 'user', action: 'update', source: 'ui', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json()).toMatchObject({ browserNotifications: { enabled: true, times: ['08:00', '18:30'] } });
    expect(entries[0]!.actorId).toEqual(member._id);
    expect(entries[0]!.entityId).toEqual(member._id);
    expect(entries[0]!.before).toEqual({ browserNotifications: { enabled: false, times: [] } });
    expect(entries[0]!.after).toEqual({ browserNotifications: { enabled: true, times: ['08:00', '18:30'] } });
    expect(await stored(member)).toEqual({ enabled: true, times: ['08:00', '18:30'] });
  });

  it('audits only the changed field of the setting', async () => {
    const { entries } = await expectAudited(
      t,
      () => put(member, member, { enabled: false, times: ['18:30', '08:00'] }),
      { entity: 'user', action: 'update', count: 1 },
    );
    expect(entries[0]!.before).toEqual({ browserNotifications: { enabled: true } });
    expect(entries[0]!.after).toEqual({ browserNotifications: { enabled: false } });
  });

  it('writes and audits nothing when the setting does not change', async () => {
    const capture = await captureWrites(t, () => put(member, member, { enabled: false, times: ['08:00', '18:30'] }));
    expect(capture.result.statusCode, capture.result.body).toBe(200);
    expect(capture.writes).toEqual([]);
    expect(capture.auditInserts).toBe(0);
  });

  it("does not let a member change someone else's moments", async () => {
    const before = await stored(admin);
    const res = await put(member, admin, { enabled: true, times: ['07:00'] });
    expect(res.statusCode).toBe(403);
    expect(res.json()).toMatchObject({ code: 'permission_denied' });
    expect(await stored(admin)).toEqual(before);
  });

  it("lets an administrator change another person's moments, attributed to the administrator", async () => {
    const { result, entries } = await expectAudited(
      t,
      () => put(admin, member, { enabled: true, times: ['07:15'] }),
      { entity: 'user', action: 'update', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(200);
    expect(entries[0]!.actorId).toEqual(admin._id);
    expect(entries[0]!.entityId).toEqual(member._id);
    expect(await stored(member)).toEqual({ enabled: true, times: ['07:15'] });
  });

  it('requires a profile', async () => {
    const res = await t.app.inject({
      method: 'PUT',
      url: `/api/users/${member._id.toHexString()}/browser-notifications`,
      payload: { enabled: true, times: [] },
    });
    expect(res.statusCode).toBe(400);
    expect(res.json()).toMatchObject({ code: 'profile_required' });
  });

  it('rejects invalid moments with field details and stores nothing', async () => {
    const before = await stored(member);
    const cases: [unknown, string][] = [
      [{ enabled: true, times: ['25:00'] }, 'times.0'],
      [{ enabled: true, times: ['8:00'] }, 'times.0'],
      [{ enabled: true, times: ['08:00', '08:00'] }, 'times'],
      [{ enabled: true, times: ['01:00', '02:00', '03:00', '04:00', '05:00', '06:00', '07:00'] }, 'times'],
      [{ times: ['08:00'] }, 'enabled'],
      [{ enabled: true }, 'times'],
    ];
    for (const [payload, field] of cases) {
      const res = await put(member, member, payload);
      expect(res.statusCode, JSON.stringify(payload)).toBe(400);
      expect(res.json<{ code: string }>()).toMatchObject({ code: 'validation_error' });
      expect(res.json<{ details: { field: string }[] }>().details.map((d) => d.field)).toContain(field);
    }
    expect(await stored(member)).toEqual(before);
  });

  it('returns 404 for an unknown person and 400 for a malformed id', async () => {
    const unknown = await t.app.inject({
      method: 'PUT',
      url: '/api/users/0123456789abcdef01234567/browser-notifications',
      headers: asProfile(admin),
      payload: { enabled: true, times: [] },
    });
    expect(unknown.statusCode).toBe(404);
    const malformed = await t.app.inject({
      method: 'PUT',
      url: '/api/users/nope/browser-notifications',
      headers: asProfile(admin),
      payload: { enabled: true, times: [] },
    });
    expect(malformed.statusCode).toBe(400);
  });
});
