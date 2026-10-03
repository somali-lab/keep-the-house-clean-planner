import { fromDayKey, type OccurrenceView } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { findOccurrences } from '../src/data/occurrences.ts';
import { findTaskById } from '../src/data/tasks.ts';
import type { UserDoc } from '../src/data/users.ts';
import { captureWrites, expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { DEFAULT_TEST_NOW, createTestApp, type TestApp } from './helpers/testApp.ts';

// Wednesday 16 Sep 2026; the nightly run generates cycles 0 (14 Sep–11 Oct) and 1 (12 Oct–8 Nov).
let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
let ramen: string;
let afwas: string;

type Body = OccurrenceView & { warnings?: { code: string }[] };

const post = (payload: Record<string, unknown>, headers: Record<string, string> = asProfile(p1)) =>
  t.app.inject({ method: 'POST', url: '/api/occurrences', headers, payload });
const retract = (id: string, headers: Record<string, string> = asProfile(p1)) =>
  t.app.inject({ method: 'POST', url: `/api/occurrences/${id}/retract`, headers });
const patch = (id: string, payload: Record<string, unknown>) =>
  t.app.inject({ method: 'PATCH', url: `/api/occurrences/${id}`, headers: asProfile(p1), payload });

async function createTask(name: string, defaultAssigneeId?: UserDoc): Promise<string> {
  const room = await seededRoom(t, 'Woonkamer');
  const res = await t.app.inject({
    method: 'POST',
    url: '/api/tasks',
    headers: asProfile(p1),
    payload: {
      name,
      roomId: room._id.toHexString(),
      intervalKey: 'quarter',
      durationMinutes: 60,
      ...(defaultAssigneeId ? { defaultAssigneeId: defaultAssigneeId._id.toHexString() } : {}),
    },
  });
  return res.json<{ _id: string }>()._id;
}

const lastCompletedAt = async (taskId: string) => (await findTaskById(t.db, new ObjectId(taskId)))?.lastCompletedAt ?? null;
const occurrencesOf = (taskId: string, status?: string) =>
  findOccurrences(t.db, { taskId: new ObjectId(taskId), ...(status ? { status: status as 'done' } : {}) });
const key = (n: number) => `extra-execution-key-${String(n).padStart(4, '0')}`;

beforeAll(async () => {
  t = await createTestApp();
  [p1, p2] = await seededUsers(t);
  ramen = await createTask('Ramen lappen', p2);
  afwas = await createTask('Afwas');
  // Afwas is also planned on Wednesday of cycle 0 (today), so a generated occurrence exists next to the extras.
  const plan = (await findActivePlan(t.db))!;
  const slots = await t.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${plan._id.toHexString()}/slots`,
    headers: asProfile(p1),
    payload: { slots: [{ taskId: afwas, weekIndex: 0, weekday: 3, assigneeId: p1._id.toHexString() }] },
  });
  expect(slots.statusCode, slots.body).toBe(200);
  expect((await t.app.inject({ method: 'POST', url: '/api/jobs/generation', headers: asProfile(p1) })).statusCode).toBe(200);
});

afterAll(async () => {
  await t.close();
});

describe('POST /api/occurrences (planned extra)', () => {
  it('plans a task on a day with the default assignee, audited as create from the UI', async () => {
    const { result, entries } = await expectAudited(t, () => post({ taskId: ramen, date: '2026-09-19' }), {
      entity: 'occurrence',
      action: 'create',
      source: 'ui',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(201);
    expect(result.json<Body>()).toMatchObject({
      taskId: ramen,
      date: '2026-09-19',
      plannedDate: '2026-09-19',
      assigneeId: p2._id.toHexString(),
      status: 'open',
      origin: 'adhoc',
      recordedDone: false,
      requestId: null,
      planId: null,
      taskNameSnapshot: 'Ramen lappen',
      roomNameSnapshot: 'Woonkamer',
      durationMinutesSnapshot: 60,
      isOverdue: false,
      movedFrom: null,
      warnings: [],
    });
    expect(entries[0]!.actorId).toEqual(p1._id);
    expect(entries[0]!.meta).toEqual({ origin: 'adhoc', kind: 'extra', recordedDone: false, requestId: null });
  });

  it('accepts an explicit "wie dan ook"', async () => {
    const res = await post({ taskId: ramen, date: '2026-09-20', assigneeId: null });
    expect(res.statusCode).toBe(201);
    expect(res.json<OccurrenceView>().assigneeId).toBeNull();
  });

  it('warns when the task is already planned that day, but still creates the second occurrence', async () => {
    const { result } = await expectAudited(t, () => post({ taskId: ramen, date: '2026-09-19' }), {
      entity: 'occurrence',
      action: 'create',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(201);
    expect(result.json<Body>().warnings).toEqual([
      expect.objectContaining({ code: 'task_already_planned', details: { taskId: ramen, date: '2026-09-19' } }),
    ]);
  });

  it('only plans within generated cycles', async () => {
    const res = await post({ taskId: ramen, date: '2026-12-01' });
    expect(res.statusCode).toBe(409);
    expect(res.json()).toMatchObject({ code: 'cycle_not_generated', date: '2026-12-01' });
  });

  it('a planned extra that is completed later still uncompletes back to open', async () => {
    const created = (await post({ taskId: ramen, date: '2026-09-21', assigneeId: p1._id.toHexString() })).json<OccurrenceView>();
    expect((await patch(created._id, { action: 'complete' })).statusCode).toBe(200);
    const open = await patch(created._id, { action: 'uncomplete' });
    expect(open.statusCode, open.body).toBe(200);
    expect(open.json<OccurrenceView>()).toMatchObject({ status: 'open', recordedDone: false, completedAt: null });
    expect((await retract(created._id)).json()).toMatchObject({ code: 'not_retractable' });
  });

  it('validates task, assignee, date and profile', async () => {
    expect((await post({ taskId: '0123456789abcdef01234567', date: '2026-09-22' })).statusCode).toBe(400);
    expect((await post({ taskId: ramen, date: '2026-09-22', assigneeId: '0123456789abcdef01234567' })).statusCode).toBe(400);
    expect((await post({ taskId: ramen, date: '22-09-2026' })).statusCode).toBe(400);
    expect((await post({ taskId: ramen, date: '2026-09-22' }, {})).statusCode).toBe(400);
    expect((await post({ taskId: ramen, date: '2026-09-22', requestId: 'short' })).statusCode).toBe(400);
  });

  it('rejects an inactive task', async () => {
    const task = await createTask('Tijdelijke taak');
    await t.app.inject({ method: 'PATCH', url: `/api/tasks/${task}`, headers: asProfile(p1), payload: { active: false } });
    const inactive = await post({ taskId: task, date: '2026-09-22' });
    expect(inactive.statusCode).toBe(400);
    expect(inactive.json()).toMatchObject({ details: [{ field: 'taskId', message: 'inactive_task' }] });
  });
});

describe('POST /api/occurrences (done now)', () => {
  it('keeps three same-day extras next to the generated occurrence; lastCompletedAt follows the newest', async () => {
    t.clock.set(DEFAULT_TEST_NOW);
    const [generated] = await occurrencesOf(afwas);
    expect(generated).toMatchObject({ origin: 'generated', status: 'open' });
    expect((await patch(generated!._id.toHexString(), { action: 'complete' })).statusCode).toBe(200);

    const ids: string[] = [];
    for (const [n, instant] of [[1, '2026-09-16T09:00:00.000Z'], [2, '2026-09-16T10:00:00.000Z'], [3, '2026-09-16T11:00:00.000Z']] as const) {
      t.clock.set(instant);
      const res = await post({ taskId: afwas, date: '2026-09-16', done: true, requestId: key(n) });
      expect(res.statusCode, res.body).toBe(201);
      ids.push(res.json<OccurrenceView>()._id);
    }

    const all = await findOccurrences(t.db, { taskId: new ObjectId(afwas), date: fromDayKey('2026-09-16') });
    expect(all).toHaveLength(4);
    expect(all.every((o) => o.status === 'done')).toBe(true);
    expect(all.filter((o) => o.origin === 'adhoc')).toHaveLength(3);
    expect(await lastCompletedAt(afwas)).toEqual(new Date('2026-09-16T11:00:00.000Z'));

    // Retracting the newest one restores the previous completion; a second retract is gone.
    const { result, entries } = await expectAudited(t, () => retract(ids[2]!), {
      entity: 'occurrence',
      action: 'delete',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json()).toEqual({ retracted: true, id: ids[2] });
    expect(entries[0]!.meta).toEqual({ reason: 'retract' });
    expect(await lastCompletedAt(afwas)).toEqual(new Date('2026-09-16T10:00:00.000Z'));
    expect(await findOccurrences(t.db, { taskId: new ObjectId(afwas), date: fromDayKey('2026-09-16') })).toHaveLength(3);

    const writes = await captureWrites(t, () => retract(ids[2]!));
    expect(writes.result.statusCode).toBe(404);
    expect(writes.writes).toEqual([]);

    // Uncomplete would leave an open record behind, so recorded work must be retracted instead.
    const blocked = await captureWrites(t, () => patch(ids[1]!, { action: 'uncomplete' }));
    expect(blocked.result.statusCode).toBe(409);
    expect(blocked.result.json()).toMatchObject({ code: 'retract_required' });
    expect(blocked.writes).toEqual([]);

    expect((await retract(ids[1]!)).statusCode).toBe(200);
    expect((await retract(ids[0]!)).statusCode).toBe(200);
    // Only the generated completion remains.
    expect(await lastCompletedAt(afwas)).toEqual(new Date('2026-09-16T08:00:00.000Z'));
  });

  it('records one done document for the actor, audited with its final fields', async () => {
    t.clock.set('2026-09-16T12:00:00.000Z');
    const { result, entries } = await expectAudited(
      t,
      () => post({ taskId: ramen, date: '2026-09-16', done: true, requestId: key(10) }),
      { entity: 'occurrence', action: 'create', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(201);
    expect(result.json<Body>()).toMatchObject({
      status: 'done',
      origin: 'adhoc',
      recordedDone: true,
      requestId: key(10),
      statusBeforeCompletion: null,
      completedAt: '2026-09-16T12:00:00.000Z',
      completedBy: p1._id.toHexString(),
      assigneeId: p1._id.toHexString(),
      warnings: [],
    });
    expect(entries[0]!.after).toMatchObject({ status: 'done', recordedDone: true, requestId: key(10) });
    expect(entries[0]!.meta).toEqual({ origin: 'adhoc', kind: 'extra', recordedDone: true, requestId: key(10) });
    expect(await lastCompletedAt(ramen)).toEqual(new Date('2026-09-16T12:00:00.000Z'));
    const due = await t.app.inject({ method: 'GET', url: '/api/due' });
    expect(due.json<{ taskId: string; daysSince: number }[]>().find((i) => i.taskId === ramen)?.daysSince).toBe(0);
  });

  it('records for an explicit person', async () => {
    t.clock.set('2026-09-16T12:30:00.000Z');
    const res = await post({ taskId: ramen, date: '2026-09-16', done: true, assigneeId: p2._id.toHexString() });
    expect(res.statusCode, res.body).toBe(201);
    expect(res.json<OccurrenceView>()).toMatchObject({ completedBy: p2._id.toHexString(), assigneeId: p2._id.toHexString() });
  });

  it('requires today and a person', async () => {
    t.clock.set('2026-09-16T13:00:00.000Z');
    const future = await captureWrites(t, () => post({ taskId: ramen, date: '2026-09-17', done: true }));
    expect(future.result.statusCode).toBe(400);
    expect(future.result.json()).toMatchObject({ code: 'validation_error', details: [{ field: 'date', message: 'done_requires_today' }] });
    const past = await post({ taskId: ramen, date: '2026-09-15', done: true });
    expect(past.json()).toMatchObject({ details: [{ field: 'date', message: 'done_requires_today' }] });
    const nobody = await captureWrites(t, () => post({ taskId: ramen, date: '2026-09-16', done: true, assigneeId: null }));
    expect(nobody.result.statusCode).toBe(400);
    expect(nobody.result.json()).toMatchObject({ details: [{ field: 'assigneeId', message: 'done_requires_person' }] });
    expect(future.writes).toEqual([]);
    expect(nobody.writes).toEqual([]);
  });

  it('warns about a planned occurrence when work is recorded as done, and leaves the planned one alone', async () => {
    t.clock.set('2026-09-16T13:30:00.000Z');
    const task = await createTask('Planten water geven');
    expect((await post({ taskId: task, date: '2026-09-16' })).statusCode).toBe(201);
    const res = await post({ taskId: task, date: '2026-09-16', done: true, requestId: key(60) });
    expect(res.statusCode).toBe(201);
    expect(res.json<Body>().warnings).toEqual([
      expect.objectContaining({ code: 'task_already_planned', details: { taskId: task, date: '2026-09-16' } }),
    ]);
    expect(await occurrencesOf(task, 'open')).toHaveLength(1);
    // A replay of the same key returns the stored record without a new warning.
    const replay = await post({ taskId: task, date: '2026-09-16', done: true, requestId: key(60) });
    expect(replay.statusCode).toBe(200);
  });
});

describe('idempotent creation', () => {
  it('replays the stored record for a repeated request: 200, one document, one audit entry, no writes', async () => {
    t.clock.set('2026-09-16T14:00:00.000Z');
    const task = await createTask('Keukenkastjes');
    const first = await expectAudited(t, () => post({ taskId: task, date: '2026-09-16', done: true, requestId: key(20) }), {
      entity: 'occurrence',
      action: 'create',
      count: 1,
    });
    expect(first.result.statusCode, first.result.body).toBe(201);

    t.clock.set('2026-09-16T14:05:00.000Z');
    const replay = await captureWrites(t, () => post({ taskId: task, date: '2026-09-16', done: true, requestId: key(20) }));
    expect(replay.result.statusCode, replay.result.body).toBe(200);
    expect(replay.result.json<OccurrenceView>()._id).toBe(first.result.json<OccurrenceView>()._id);
    expect(replay.writes).toEqual([]);
    expect(replay.auditInserts).toBe(0);

    expect(await findOccurrences(t.db, { requestId: key(20) })).toHaveLength(1);
    const audit = await t.db.collection(COLLECTIONS.auditLog).countDocuments({
      entity: 'occurrence',
      action: 'create',
      entityId: new ObjectId(first.result.json<OccurrenceView>()._id),
    });
    expect(audit).toBe(1);
    expect(await lastCompletedAt(task)).toEqual(new Date('2026-09-16T14:00:00.000Z'));
  });

  it('rejects a reused key for a different request without writing', async () => {
    t.clock.set('2026-09-16T15:00:00.000Z');
    const task = await createTask('Spiegels poetsen');
    const other = await createTask('Stofzuigen trap');
    expect((await post({ taskId: task, date: '2026-09-16', requestId: key(30) })).statusCode).toBe(201);

    const differentDate = await captureWrites(t, () => post({ taskId: task, date: '2026-09-17', requestId: key(30) }));
    const differentTask = await post({ taskId: other, date: '2026-09-16', requestId: key(30) });
    const differentDone = await post({ taskId: task, date: '2026-09-16', done: true, requestId: key(30) });
    for (const res of [differentDate.result, differentTask, differentDone]) {
      expect(res.statusCode, res.body).toBe(409);
      expect(res.json()).toMatchObject({ code: 'idempotency_key_conflict' });
    }
    expect(differentDate.writes).toEqual([]);
    expect(await findOccurrences(t.db, { requestId: key(30) })).toHaveLength(1);
  });

  it('creates the record again when the key is reused after a retract', async () => {
    t.clock.set('2026-09-16T16:00:00.000Z');
    const task = await createTask('Deurmatten uitkloppen');
    const first = (await post({ taskId: task, date: '2026-09-16', done: true, requestId: key(40) })).json<OccurrenceView>();
    expect((await retract(first._id)).statusCode).toBe(200);
    const again = await post({ taskId: task, date: '2026-09-16', done: true, requestId: key(40) });
    expect(again.statusCode, again.body).toBe(201);
    expect(again.json<OccurrenceView>()._id).not.toBe(first._id);
  });
});

describe('POST /api/occurrences/:id/retract', () => {
  it('refuses work that was not recorded as done, and needs a profile', async () => {
    const [generated] = await occurrencesOf(afwas, 'done');
    const res = await captureWrites(t, () => retract(generated!._id.toHexString()));
    expect(res.result.statusCode).toBe(409);
    expect(res.result.json()).toMatchObject({ code: 'not_retractable' });
    expect(res.writes).toEqual([]);

    expect((await retract('0123456789abcdef01234567')).statusCode).toBe(404);
    expect((await retract(generated!._id.toHexString(), {})).statusCode).toBe(400);
  });

  it('only retracts work of today: a record of an earlier day is refused and stays', async () => {
    t.clock.set('2026-09-16T16:00:00.000Z');
    const task = await createTask('Plinten afnemen');
    const recorded = (await post({ taskId: task, date: '2026-09-16', done: true, requestId: key(50) })).json<OccurrenceView>();

    // The next day it is history: retracting is an undo of today's work, and older completions need an administrator.
    t.clock.set('2026-09-17T08:00:00.000Z');
    const refused = await captureWrites(t, () => retract(recorded._id, asProfile(p2)));
    expect(refused.result.statusCode).toBe(409);
    expect(refused.result.json()).toMatchObject({ code: 'retract_not_today' });
    expect(refused.writes).toEqual([]);
    expect(await occurrencesOf(task, 'done')).toHaveLength(1);
    expect(await lastCompletedAt(task)).not.toBeNull();

    t.clock.set(DEFAULT_TEST_NOW);
  });
});
