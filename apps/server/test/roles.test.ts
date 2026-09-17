import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { UserDoc } from '../src/data/users.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;
let admin: UserDoc;
let member: UserDoc;
let roomId: string;

beforeAll(async () => {
  t = await createTestApp();
  [admin, member] = await seededUsers(t);
  roomId = (await seededRoom(t, 'Keuken'))._id.toHexString();
});

afterAll(async () => {
  await t.close();
});

describe('role permissions without passwords', () => {
  it('seeds one administrator and household members', () => {
    expect(admin.role).toBe('admin');
    expect(member.role).toBe('member');
  });

  it('lets an administrator assign the planner role', async () => {
    const response = await t.app.inject({
      method: 'POST',
      url: '/api/users',
      headers: asProfile(admin),
      payload: { name: 'Planner', color: '#16a34a', role: 'planner' },
    });
    expect(response.statusCode, response.body).toBe(201);
    expect(response.json()).toMatchObject({ name: 'Planner', role: 'planner' });
  });

  it('allows planners to manage tasks but not people', async () => {
    const users = await t.app.inject({ method: 'GET', url: '/api/users' });
    const planner = users.json<{ _id: string; name: string }[]>().find((user) => user.name === 'Planner')!;
    const headers = { 'x-profile-id': planner._id, 'x-client': 'web' };

    const task = await t.app.inject({
      method: 'POST',
      url: '/api/tasks',
      headers,
      payload: { name: 'Aanrecht', roomId, intervalKey: '1w', durationMinutes: 10 },
    });
    expect(task.statusCode, task.body).toBe(201);

    const user = await t.app.inject({
      method: 'POST',
      url: '/api/users',
      headers,
      payload: { name: 'Niet toegestaan', color: '#000000' },
    });
    expect(user.statusCode).toBe(403);
    expect(user.json()).toMatchObject({ code: 'permission_denied' });
  });

  it('lets members do household work but blocks planning and configuration', async () => {
    const task = await t.app.inject({
      method: 'POST',
      url: '/api/tasks',
      headers: asProfile(admin),
      payload: { name: 'Afwas', roomId, intervalKey: '1w', durationMinutes: 10 },
    });
    const taskId = task.json<{ _id: string }>()._id;

    const generated = await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(admin) });
    expect(generated.statusCode, generated.body).toBe(200);

    const occurrence = await t.app.inject({
      method: 'POST',
      url: '/api/occurrences',
      headers: asProfile(member),
      payload: { taskId, date: '2026-09-17', assigneeId: member._id.toHexString() },
    });
    expect(occurrence.statusCode, occurrence.body).toBe(201);

    const forbidden = await Promise.all([
      t.app.inject({
        method: 'POST',
        url: '/api/tasks',
        headers: asProfile(member),
        payload: { name: 'Verboden', roomId, intervalKey: '1w', durationMinutes: 10 },
      }),
      t.app.inject({ method: 'PATCH', url: '/api/settings', headers: asProfile(member), payload: { promoteThreshold: 3 } }),
    ]);
    expect(forbidden.map((response) => [response.statusCode, response.json().code])).toEqual([
      [403, 'permission_denied'],
      [403, 'permission_denied'],
    ]);
  });
});
