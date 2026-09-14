import { DEFAULT_INTERVALS } from '@huishoudplanner/shared';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { COLLECTIONS } from '../src/data/db.ts';
import type { RoomDoc } from '../src/data/rooms.ts';
import type { UserDoc } from '../src/data/users.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
let badkamer: RoomDoc;
let keuken: RoomDoc;

interface TaskJson {
  _id: string;
  name: string;
  roomId: string;
  intervalKey: string;
  durationMinutes: number;
  defaultAssigneeId: string | null;
  active: boolean;
  lastCompletedAt: string | null;
}

beforeAll(async () => {
  t = await createTestApp();
  [p1, p2] = await seededUsers(t);
  badkamer = await seededRoom(t, 'Badkamer');
  keuken = await seededRoom(t, 'Keuken');
});

afterAll(async () => {
  await t.close();
});

function post(url: string, payload: Record<string, unknown>) {
  return t.app.inject({ method: 'POST', url, headers: asProfile(p1), payload });
}

function patch(url: string, payload: Record<string, unknown>) {
  return t.app.inject({ method: 'PATCH', url, headers: asProfile(p1), payload });
}

function del(url: string) {
  return t.app.inject({ method: 'DELETE', url, headers: asProfile(p1) });
}

async function newTask(overrides: Record<string, unknown> = {}): Promise<TaskJson> {
  const res = await post('/api/tasks', {
    name: 'Badkamer schoonmaken',
    roomId: badkamer._id.toHexString(),
    intervalKey: '1w',
    durationMinutes: 30,
    ...overrides,
  });
  expect(res.statusCode).toBe(201);
  return res.json<TaskJson>();
}

describe('POST /api/tasks', () => {
  it('creates a task with defaults and audits every field', async () => {
    const { result, entries } = await expectAudited(
      t,
      () =>
        post('/api/tasks', {
          name: 'Badkamer schoonmaken',
          roomId: badkamer._id.toHexString(),
          intervalKey: '1w',
          durationMinutes: 30,
          tags: ['nat'],
        }),
      { entity: 'task', action: 'create', source: 'ui', count: 1 },
    );
    expect(result.statusCode).toBe(201);
    const task = result.json<TaskJson>();
    expect(task).toMatchObject({ defaultAssigneeId: null, active: true, lastCompletedAt: null });
    expect(entries[0]!.after).toEqual({
      name: 'Badkamer schoonmaken',
      roomId: badkamer._id,
      intervalKey: '1w',
      durationMinutes: 30,
      defaultAssigneeId: null,
      notes: '',
      tags: ['nat'],
      active: true,
      lastCompletedAt: null,
    });
  });

  it.each([
    ['missing duration', { durationMinutes: undefined }, ['durationMinutes']],
    ['zero duration', { durationMinutes: 0 }, ['durationMinutes']],
    ['fractional duration', { durationMinutes: 12.5 }, ['durationMinutes']],
    ['missing name, room and interval', { name: undefined, roomId: undefined, intervalKey: undefined }, [
      'name',
      'roomId',
      'intervalKey',
    ]],
  ])('rejects %s with 400 naming the fields', async (_label, overrides, fields) => {
    const payload = {
      name: 'X',
      roomId: badkamer._id.toHexString(),
      intervalKey: '1w',
      durationMinutes: 10,
      ...overrides,
    };
    const res = await post('/api/tasks', JSON.parse(JSON.stringify(payload)));
    expect(res.statusCode).toBe(400);
    const got = res.json<{ code: string; details: { field: string }[] }>();
    expect(got.code).toBe('validation_error');
    expect(got.details.map((d) => d.field)).toEqual(expect.arrayContaining(fields));
  });

  it('rejects unknown interval, room and assignee references', async () => {
    const res = await post('/api/tasks', {
      name: 'X',
      roomId: '0123456789abcdef01234567',
      intervalKey: 'fortnightly',
      durationMinutes: 10,
      defaultAssigneeId: '0123456789abcdef01234568',
    });
    expect(res.statusCode).toBe(400);
    expect(res.json<{ details: unknown }>().details).toEqual([
      { field: 'roomId', message: 'unknown_room' },
      { field: 'intervalKey', message: 'unknown_interval' },
      { field: 'defaultAssigneeId', message: 'unknown_user' },
    ]);
    expect(await t.db.collection(COLLECTIONS.tasks).countDocuments({ name: 'X' })).toBe(0);
  });

  it('accepts an interval added via settings (year) without code changes', async () => {
    const settings = await patch('/api/settings', {
      intervals: [...DEFAULT_INTERVALS, { key: 'year', label: '1x per jaar', perCycle: null, periodDays: 365 }],
    });
    expect(settings.statusCode).toBe(200);
    const task = await newTask({ name: 'Matras keren', intervalKey: 'year', durationMinutes: 15 });
    expect(task.intervalKey).toBe('year');
  });
});

describe('PATCH /api/tasks/:id', () => {
  it('audits old and new values of name, interval, duration and room', async () => {
    const task = await newTask();
    const { result, entries } = await expectAudited(
      t,
      () =>
        patch(`/api/tasks/${task._id}`, {
          name: 'Badkamer grondig',
          intervalKey: '2wk',
          durationMinutes: 45,
          roomId: keuken._id.toHexString(),
        }),
      { entity: 'task', action: 'update', count: 1 },
    );
    expect(result.statusCode).toBe(200);
    expect(entries[0]!.before).toEqual({
      name: 'Badkamer schoonmaken',
      intervalKey: '1w',
      durationMinutes: 30,
      roomId: badkamer._id,
    });
    expect(entries[0]!.after).toEqual({
      name: 'Badkamer grondig',
      intervalKey: '2wk',
      durationMinutes: 45,
      roomId: keuken._id,
    });
  });

  it('logs a defaultAssigneeId change as action assign', async () => {
    const task = await newTask();
    const { entries } = await expectAudited(
      t,
      () => patch(`/api/tasks/${task._id}`, { defaultAssigneeId: p2._id.toHexString() }),
      { entity: 'task', action: 'assign', count: 1 },
    );
    expect(entries[0]!.before).toEqual({ defaultAssigneeId: null });
    expect(entries[0]!.after).toEqual({ defaultAssigneeId: p2._id });
    const noUpdate = await t.db
      .collection(COLLECTIONS.auditLog)
      .countDocuments({ entityId: entries[0]!.entityId, action: 'update' });
    expect(noUpdate).toBe(0);
  });

  it('writes separate update and assign entries when both change', async () => {
    const task = await newTask();
    const { entries } = await expectAudited(
      t,
      () => patch(`/api/tasks/${task._id}`, { durationMinutes: 20, defaultAssigneeId: p1._id.toHexString() }),
      { entity: 'task', action: 'assign', count: 1 },
    );
    const update = await t.db
      .collection(COLLECTIONS.auditLog)
      .findOne({ entityId: entries[0]!.entityId, action: 'update' });
    expect(update?.after).toEqual({ durationMinutes: 20 });
  });

  it('validates duration and references on update; 404 for unknown task', async () => {
    const task = await newTask();
    const bad = await patch(`/api/tasks/${task._id}`, { durationMinutes: 0 });
    expect(bad.statusCode).toBe(400);
    const badInterval = await patch(`/api/tasks/${task._id}`, { intervalKey: 'nope' });
    expect(badInterval.statusCode).toBe(400);
    const unknown = await patch('/api/tasks/0123456789abcdef01234567', { name: 'X' });
    expect(unknown.statusCode).toBe(404);
  });
});

describe('GET /api/tasks', () => {
  it('filters by room and active', async () => {
    const room = await post('/api/rooms', { name: 'Berging' });
    const roomId = room.json<{ _id: string }>()._id;
    const a = await newTask({ name: 'Berging opruimen', roomId });
    const b = await newTask({ name: 'Berging vegen', roomId });
    await patch(`/api/tasks/${b._id}`, { active: false });

    const inRoom = await t.app.inject({ method: 'GET', url: `/api/tasks?roomId=${roomId}` });
    expect(inRoom.json<TaskJson[]>().map((x) => x.name).sort()).toEqual(['Berging opruimen', 'Berging vegen']);
    const activeOnly = await t.app.inject({ method: 'GET', url: `/api/tasks?roomId=${roomId}&active=true` });
    expect(activeOnly.json<TaskJson[]>().map((x) => x._id)).toEqual([a._id]);
    const invalid = await t.app.inject({ method: 'GET', url: '/api/tasks?active=yes' });
    expect(invalid.statusCode).toBe(400);
  });
});

describe('DELETE /api/tasks/:id', () => {
  it('permanently removes a task and audits its previous values', async () => {
    const task = await newTask({ name: 'Wegwerpklus' });
    const { result, entries } = await expectAudited(
      t,
      () => del(`/api/tasks/${task._id}`),
      { entity: 'task', action: 'delete', count: 1 },
    );
    expect(result.statusCode).toBe(200);
    expect(result.json()).toEqual({ deleted: true });
    expect(entries[0]!.before.name).toBe('Wegwerpklus');
    expect((await t.app.inject({ method: 'GET', url: `/api/tasks?roomId=${badkamer._id.toHexString()}` })).json<TaskJson[]>())
      .not.toEqual(expect.arrayContaining([expect.objectContaining({ _id: task._id })]));
  });
});

describe('POST /api/rooms/:id/tasks/bulk', () => {
  async function roomWithTasks(name: string, count: number) {
    const room = await post('/api/rooms', { name });
    const roomId = room.json<{ _id: string }>()._id;
    const tasks: TaskJson[] = [];
    for (let i = 0; i < count; i++) tasks.push(await newTask({ name: `${name} ${i}`, roomId }));
    return { roomId, tasks };
  }

  it('deactivates all tasks in the room with one audit entry per task', async () => {
    const { roomId, tasks } = await roomWithTasks('Schuur', 3);
    const { result, entries } = await expectAudited(
      t,
      () => post(`/api/rooms/${roomId}/tasks/bulk`, { op: 'deactivate' }),
      { entity: 'task', action: 'update', count: 3 },
    );
    expect(result.json()).toEqual({ updated: 3 });
    expect(entries.map((e) => e.entityId.toHexString()).sort()).toEqual(tasks.map((x) => x._id).sort());
    expect(entries.every((e) => e.before.active === true && e.after.active === false)).toBe(true);
    const list = await t.app.inject({ method: 'GET', url: `/api/tasks?roomId=${roomId}&active=true` });
    expect(list.json()).toEqual([]);
  });

  it('reassigns all active tasks in the room, logged as assign per task', async () => {
    const { roomId } = await roomWithTasks('Tuin', 2);
    const { result, entries } = await expectAudited(
      t,
      () => post(`/api/rooms/${roomId}/tasks/bulk`, { op: 'reassign', defaultAssigneeId: p2._id.toHexString() }),
      { entity: 'task', action: 'assign', count: 2 },
    );
    expect(result.json()).toEqual({ updated: 2 });
    expect(entries.every((e) => p2._id.equals(e.after.defaultAssigneeId as never))).toBe(true);
  });

  it('validates op, assignee and room', async () => {
    const { roomId } = await roomWithTasks('Garage', 1);
    expect((await post(`/api/rooms/${roomId}/tasks/bulk`, { op: 'explode' })).statusCode).toBe(400);
    expect(
      (await post(`/api/rooms/${roomId}/tasks/bulk`, { op: 'reassign', defaultAssigneeId: '0123456789abcdef01234567' }))
        .statusCode,
    ).toBe(400);
    expect((await post('/api/rooms/0123456789abcdef01234567/tasks/bulk', { op: 'deactivate' })).statusCode).toBe(404);
  });
});
