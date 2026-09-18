import type { OccurrenceView } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { findTaskById } from '../src/data/tasks.ts';
import type { UserDoc } from '../src/data/users.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

// Generated on Monday 14 Sep; tests run on Wednesday 16 Sep 10:00 Amsterdam.
let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
let weekly: string;
let twice: string;

async function inject(
  method: 'GET' | 'PATCH' | 'POST' | 'PUT',
  url: string,
  payload?: Record<string, unknown>,
  user = p1,
) {
  return t.app.inject({ method, url, headers: asProfile(user), ...(payload ? { payload } : {}) });
}

async function list(query: string): Promise<OccurrenceView[]> {
  const res = await inject('GET', `/api/occurrences?${query}`);
  expect(res.statusCode, res.body).toBe(200);
  return res.json<OccurrenceView[]>();
}

async function find(taskId: string, date: string): Promise<OccurrenceView> {
  const occ = (await list(`from=${date}&to=${date}`)).find((o) => o.taskId === taskId);
  if (!occ) throw new Error(`no occurrence for ${taskId} on ${date}`);
  return occ;
}

const patch = (id: string, payload: Record<string, unknown>, user = p1) =>
  inject('PATCH', `/api/occurrences/${id}`, payload, user);

beforeAll(async () => {
  t = await createTestApp({ now: '2026-09-14T06:00:00.000Z' });
  [p1, p2] = await seededUsers(t);
  const room = await seededRoom(t, 'Badkamer');
  const createTask = async (name: string, intervalKey: string, durationMinutes: number) =>
    (
      await inject('POST', '/api/tasks', { name, roomId: room._id.toHexString(), intervalKey, durationMinutes })
    ).json<{ _id: string }>()._id;
  weekly = await createTask('Badkamer schoonmaken', '1w', 30);
  twice = await createTask('Wastafel', '2w', 10);
  const planId = (await findActivePlan(t.db))!._id.toHexString();
  const P1 = p1._id.toHexString();
  const P2 = p2._id.toHexString();
  const slots = [
    ...[0, 1, 2, 3].map((w) => ({ taskId: weekly, weekIndex: w, weekday: 1, assigneeId: P1 })),
    ...[0, 1, 2, 3].flatMap((w) => [
      { taskId: twice, weekIndex: w, weekday: 3, assigneeId: null },
      { taskId: twice, weekIndex: w, weekday: 4, assigneeId: P2 },
    ]),
    { taskId: weekly, weekIndex: 0, weekday: 3, assigneeId: P1 },
  ];
  const put = await inject('PUT', `/api/cycle-plans/${planId}/slots`, { slots });
  expect(put.statusCode, put.body).toBe(200);
  expect((await inject('POST', '/api/jobs/nightly')).statusCode).toBe(200);
  t.clock.set('2026-09-16T08:00:00.000Z');
});

afterAll(async () => {
  await t.close();
});

describe('GET /api/occurrences', () => {
  it('returns day keys with isOverdue and movedFrom for a date range', async () => {
    const week = await list('from=2026-09-14&to=2026-09-20');
    expect(week.map((o) => [o.date, o.taskNameSnapshot])).toEqual([
      ['2026-09-14', 'Badkamer schoonmaken'],
      ['2026-09-16', 'Badkamer schoonmaken'],
      ['2026-09-16', 'Wastafel'],
      ['2026-09-17', 'Wastafel'],
    ]);
    expect(week.map((o) => o.isOverdue)).toEqual([true, false, false, false]);
    expect(week.every((o) => o.movedFrom === null && o.plannedDate === o.date)).toBe(true);
  });

  it('filters by assignee and status', async () => {
    const mine = await list(`from=2026-09-14&to=2026-09-20&assigneeId=${p2._id.toHexString()}`);
    expect(mine.map((o) => o.date)).toEqual(['2026-09-17']);
    const open = await list('from=2026-09-14&to=2026-09-14&status=done');
    expect(open).toEqual([]);
  });

  it('validates the query', async () => {
    expect((await inject('GET', '/api/occurrences?from=2026-09-20&to=2026-09-14')).statusCode).toBe(400);
    expect((await inject('GET', '/api/occurrences?from=2026-9-1&to=2026-09-14')).statusCode).toBe(400);
    expect((await inject('GET', '/api/occurrences?from=2026-09-14')).statusCode).toBe(400);
  });
});

describe('PATCH /api/occurrences/:id', () => {
  it('credits the assignee when another actor checks off and maintains task.lastCompletedAt', async () => {
    const occ = await find(weekly, '2026-09-21');
    const { result, entries } = await expectAudited(t, () => patch(occ._id, { action: 'complete' }, p2), {
      entity: 'occurrence',
      action: 'complete',
      source: 'ui',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json()).toMatchObject({
      status: 'done',
      statusBeforeCompletion: 'open',
      completedBy: p1._id.toHexString(),
      completedAt: '2026-09-16T08:00:00.000Z',
    });
    expect(entries[0]!.meta).toMatchObject({
      completedBy: p1._id,
      wasAssignee: true,
      occurrence: {
        taskNameSnapshot: occ.taskNameSnapshot,
        roomNameSnapshot: occ.roomNameSnapshot,
        date: expect.any(Date),
      },
    });
    expect(entries[0]!.actorId).toEqual(p2._id);
    expect((await findTaskById(t.db, new ObjectId(weekly)))?.lastCompletedAt).toEqual(new Date('2026-09-16T08:00:00Z'));
  });

  it('logs both the actor and a different completedBy', async () => {
    const occ = await find(weekly, '2026-09-28');
    const { entries } = await expectAudited(
      t,
      () => patch(occ._id, { action: 'complete', completedBy: p2._id.toHexString() }),
      { entity: 'occurrence', action: 'complete', count: 1 },
    );
    expect(entries[0]!.actorId).toEqual(p1._id);
    expect(entries[0]!.meta).toMatchObject({
      completedBy: p2._id,
      wasAssignee: false,
      occurrence: {
        taskNameSnapshot: occ.taskNameSnapshot,
        roomNameSnapshot: occ.roomNameSnapshot,
      },
    });
    expect(entries[0]!.meta?.occurrence).toMatchObject({ date: expect.any(Date) });
    expect(entries[0]!.after).toMatchObject({ completedBy: p2._id });
  });

  it('claims an unclaimed occurrence implicitly for completedBy', async () => {
    const occ = await find(twice, '2026-09-23');
    expect(occ.assigneeId).toBeNull();
    const res = await patch(occ._id, { action: 'complete', completedBy: p2._id.toHexString() });
    expect(res.json()).toMatchObject({ assigneeId: p2._id.toHexString(), completedBy: p2._id.toHexString() });
  });

  it('undo after skip → done restores skipped, as a new audit entry', async () => {
    const occ = await find(twice, '2026-09-24');
    await expectAudited(t, () => patch(occ._id, { action: 'skip', reason: 'geen tijd' }), {
      entity: 'occurrence',
      action: 'skip',
      count: 1,
    });
    const done = await patch(occ._id, { action: 'complete' });
    expect(done.json()).toMatchObject({ status: 'done', statusBeforeCompletion: 'skipped' });

    const { result } = await expectAudited(t, () => patch(occ._id, { action: 'uncomplete' }), {
      entity: 'occurrence',
      action: 'uncomplete',
      count: 1,
    });
    const uncompleteEntry = await t.db.collection(COLLECTIONS.auditLog).findOne({
      entityId: new ObjectId(occ._id),
      action: 'uncomplete',
    });
    expect(uncompleteEntry?.meta?.occurrence).toMatchObject({
      taskNameSnapshot: occ.taskNameSnapshot,
      roomNameSnapshot: occ.roomNameSnapshot,
      date: expect.any(Date),
    });
    expect(result.json()).toMatchObject({
      status: 'skipped',
      skipReason: 'geen tijd',
      statusBeforeCompletion: null,
      completedAt: null,
      completedBy: null,
    });
    const history = await t.db
      .collection(COLLECTIONS.auditLog)
      .find({ entityId: new ObjectId(occ._id) })
      .sort({ _id: 1 })
      .toArray();
    expect(history.map((h) => h.action)).toEqual(['create', 'skip', 'complete', 'uncomplete']);
  });

  it('keeps lastCompletedAt correct across a complete/uncomplete sequence', async () => {
    const a = await find(weekly, '2026-10-05');
    const b = await find(weekly, '2026-10-12');
    const taskDoc = () => findTaskById(t.db, new ObjectId(weekly));
    // previous tests completed two weekly occurrences at 2026-09-16T08:00Z
    const baseline = (await taskDoc())!.lastCompletedAt;

    t.clock.set('2026-09-17T08:00:00.000Z');
    await patch(a._id, { action: 'complete' });
    t.clock.set('2026-09-18T08:00:00.000Z');
    await patch(b._id, { action: 'complete' });
    expect((await taskDoc())!.lastCompletedAt).toEqual(new Date('2026-09-18T08:00:00Z'));

    await patch(b._id, { action: 'uncomplete' });
    expect((await taskDoc())!.lastCompletedAt).toEqual(new Date('2026-09-17T08:00:00Z'));
    await patch(a._id, { action: 'uncomplete' });
    expect((await taskDoc())!.lastCompletedAt).toEqual(baseline);

    const lastAudit = await t.db
      .collection(COLLECTIONS.auditLog)
      .find({ entity: 'task', entityId: new ObjectId(weekly), action: 'update' })
      .sort({ _id: -1 })
      .limit(1)
      .toArray();
    expect(lastAudit[0]?.after).toEqual({ lastCompletedAt: baseline });
    t.clock.set('2026-09-16T08:00:00.000Z');
  });

  it('rejects invalid transitions with 409', async () => {
    const occ = await find(weekly, '2026-10-19');
    expect((await patch(occ._id, { action: 'uncomplete' })).statusCode).toBe(409);
    await patch(occ._id, { action: 'complete' });
    expect((await patch(occ._id, { action: 'complete' })).statusCode).toBe(409);
    expect((await patch(occ._id, { action: 'skip' })).statusCode).toBe(409);
  });

  it('validates body, completedBy and id', async () => {
    const occ = await find(weekly, '2026-10-26');
    expect((await patch(occ._id, { action: 'explode' })).statusCode).toBe(400);
    const badUser = await patch(occ._id, { action: 'complete', completedBy: '0123456789abcdef01234567' });
    expect(badUser.statusCode).toBe(400);
    expect(badUser.json()).toMatchObject({ details: [{ field: 'completedBy', message: 'unknown_user' }] });
    expect((await patch('0123456789abcdef01234567', { action: 'skip' })).statusCode).toBe(404);
    const anonymous = await t.app.inject({ method: 'PATCH', url: `/api/occurrences/${occ._id}`, payload: { action: 'skip' } });
    expect(anonymous.statusCode).toBe(400);
  });

  it('validates reschedule and assign payloads (behaviour is covered in reschedule.test.ts)', async () => {
    const occ = await find(weekly, '2026-11-02');
    expect((await patch(occ._id, { action: 'reschedule' })).statusCode).toBe(400);
    expect((await patch(occ._id, { action: 'assign' })).statusCode).toBe(400);
    expect((await patch(occ._id, { action: 'reschedule', date: '2026-11-03' })).statusCode).toBe(200);
  });
});

describe('POST /api/occurrences/:id/claim', () => {
  it('claims an unassigned occurrence for the actor; a second claim is 409', async () => {
    const occ = await find(twice, '2026-09-30');
    const { result } = await expectAudited(t, () => inject('POST', `/api/occurrences/${occ._id}/claim`, undefined, p2), {
      entity: 'occurrence',
      action: 'assign',
      count: 1,
    });
    expect(result.statusCode).toBe(200);
    expect(result.json()).toMatchObject({ assigneeId: p2._id.toHexString() });
    const again = await inject('POST', `/api/occurrences/${occ._id}/claim`);
    expect(again.statusCode).toBe(409);
    expect(again.json()).toMatchObject({ code: 'already_claimed' });
  });

  it('cannot claim an occurrence that already has an assignee, 404 if unknown', async () => {
    const occ = await find(twice, '2026-10-01');
    expect((await inject('POST', `/api/occurrences/${occ._id}/claim`)).statusCode).toBe(409);
    expect((await inject('POST', '/api/occurrences/0123456789abcdef01234567/claim')).statusCode).toBe(404);
  });

  it('resolves a claim race: exactly one 200 and one 409', async () => {
    const occ = await find(twice, '2026-10-07');
    const { result } = await expectAudited(
      t,
      () =>
        Promise.all([
          inject('POST', `/api/occurrences/${occ._id}/claim`, undefined, p1),
          inject('POST', `/api/occurrences/${occ._id}/claim`, undefined, p2),
        ]),
      { entity: 'occurrence', action: 'assign', count: 1 },
    );
    expect(result.map((r) => r.statusCode).sort()).toEqual([200, 409]);
    const winner = result.find((r) => r.statusCode === 200)!.json<OccurrenceView>();
    const stored = await find(twice, '2026-10-07');
    expect(stored.assigneeId).toBe(winner.assigneeId);
  });
});
