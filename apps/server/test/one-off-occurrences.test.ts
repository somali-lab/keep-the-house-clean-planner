import type { OccurrenceView } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { findOccurrences } from '../src/data/occurrences.ts';
import type { UserDoc } from '../src/data/users.ts';
import { deterministicPlan } from '../src/domain/ai/mockResponders.ts';
import type { PlanPromptPayload } from '../src/domain/ai/prompt.ts';
import { MockProvider } from '../src/domain/ai/providers/mock.ts';
import { captureWrites, expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, DEFAULT_TEST_NOW, type TestApp } from './helpers/testApp.ts';

// Wednesday 16 Sep 2026; the nightly run generates cycles 0 (14 Sep–11 Oct) and 1 (12 Oct–8 Nov).
let t: TestApp;
let provider: MockProvider;
let p1: UserDoc;
let p2: UserDoc;
let woonkamer: string;
let weekly: string;

type Body = OccurrenceView & { warnings?: { code: string }[] };

const post = (payload: Record<string, unknown>, headers: Record<string, string> = asProfile(p1)) =>
  t.app.inject({ method: 'POST', url: '/api/occurrences/one-off', headers, payload });
const retract = (id: string) =>
  t.app.inject({ method: 'POST', url: `/api/occurrences/${id}/retract`, headers: asProfile(p1) });
const patch = (id: string, payload: Record<string, unknown>) =>
  t.app.inject({ method: 'PATCH', url: `/api/occurrences/${id}`, headers: asProfile(p1), payload });
const get = (url: string) => t.app.inject({ method: 'GET', url });
const key = (n: number) => `one-off-request-key-${String(n).padStart(4, '0')}`;
const oneOffs = () => findOccurrences(t.db, { taskId: null });

beforeAll(async () => {
  provider = new MockProvider({
    responders: { 'plan-proposal': (request) => JSON.stringify(deterministicPlan(JSON.parse(request.user) as PlanPromptPayload)) },
  });
  t = await createTestApp({ aiProvider: provider });
  [p1, p2] = await seededUsers(t);
  woonkamer = (await seededRoom(t, 'Woonkamer'))._id.toHexString();
  const created = await t.app.inject({
    method: 'POST',
    url: '/api/tasks',
    headers: asProfile(p1),
    payload: { name: 'Afwas', roomId: woonkamer, intervalKey: 'quarter', durationMinutes: 20 },
  });
  weekly = created.json<{ _id: string }>()._id;
  const plan = (await findActivePlan(t.db))!;
  const slots = await t.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${plan._id.toHexString()}/slots`,
    headers: asProfile(p1),
    payload: { slots: [{ taskId: weekly, weekIndex: 0, weekday: 3, assigneeId: p1._id.toHexString() }] },
  });
  expect(slots.statusCode, slots.body).toBe(200);
  expect((await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(p1) })).statusCode).toBe(200);
});

afterAll(async () => {
  await t.close();
});

describe('POST /api/occurrences/one-off (planned)', () => {
  it('plans a one-off task from snapshots only, unassigned by default, audited as one_off', async () => {
    const taskCount = await t.db.collection(COLLECTIONS.tasks).countDocuments();
    const { result, entries } = await expectAudited(
      t,
      () => post({ name: '  Gordijnen ophangen ', roomId: woonkamer, durationMinutes: 40, date: '2026-09-19' }),
      { entity: 'occurrence', action: 'create', source: 'ui', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(201);
    expect(result.json<Body>()).toMatchObject({
      taskId: null,
      date: '2026-09-19',
      plannedDate: '2026-09-19',
      assigneeId: null,
      status: 'open',
      origin: 'adhoc',
      recordedDone: false,
      requestId: null,
      planId: null,
      taskNameSnapshot: 'Gordijnen ophangen',
      roomIdSnapshot: woonkamer,
      roomNameSnapshot: 'Woonkamer',
      durationMinutesSnapshot: 40,
      warnings: [],
    });
    expect(entries[0]!.meta).toEqual({ origin: 'adhoc', kind: 'one_off', recordedDone: false, requestId: null });
    expect(entries[0]!.after).toMatchObject({ taskId: null, taskNameSnapshot: 'Gordijnen ophangen' });
    // No task document is created.
    expect(await t.db.collection(COLLECTIONS.tasks).countDocuments()).toBe(taskCount);
  });

  it('stores a missing room as null snapshots and accepts an assignee or "wie dan ook"', async () => {
    const res = await post({ name: 'Kast ophalen', durationMinutes: 25, date: '2026-09-20', assigneeId: p2._id.toHexString() });
    expect(res.statusCode, res.body).toBe(201);
    expect(res.json<Body>()).toMatchObject({ taskId: null, roomIdSnapshot: null, roomNameSnapshot: null, assigneeId: p2._id.toHexString() });
    const explicitNull = await post({ name: 'Kast ophalen', roomId: null, durationMinutes: 25, date: '2026-09-20', assigneeId: null });
    expect(explicitNull.statusCode, explicitNull.body).toBe(201);
    expect(explicitNull.json<Body>()).toMatchObject({ roomIdSnapshot: null, assigneeId: null });
  });

  it('allows several identical one-off tasks on one day; none of them warns', async () => {
    const first = await post({ name: 'Dubbele klus', durationMinutes: 5, date: '2026-09-21' });
    const second = await post({ name: 'Dubbele klus', durationMinutes: 5, date: '2026-09-21' });
    expect(first.statusCode).toBe(201);
    expect(second.statusCode).toBe(201);
    expect(second.json<Body>().warnings).toEqual([]);
  });

  it('validates input, room, assignee, cycle and profile without writing', async () => {
    const room = await t.app.inject({ method: 'POST', url: '/api/rooms', headers: asProfile(p1), payload: { name: 'Zolder' } });
    const zolder = room.json<{ _id: string }>()._id;
    await t.app.inject({ method: 'PATCH', url: `/api/rooms/${zolder}`, headers: asProfile(p1), payload: { active: false } });
    const base = { name: 'Klus', durationMinutes: 10, date: '2026-09-22' };

    const inactive = await captureWrites(t, () => post({ ...base, roomId: zolder }));
    expect(inactive.result.statusCode).toBe(400);
    expect(inactive.result.json()).toMatchObject({ code: 'validation_error', details: [{ field: 'roomId', message: 'inactive_room' }] });
    expect(inactive.writes).toEqual([]);

    const unknown = await post({ ...base, roomId: '0123456789abcdef01234567' });
    expect(unknown.json()).toMatchObject({ details: [{ field: 'roomId', message: 'unknown_room' }] });
    const user = await post({ ...base, assigneeId: '0123456789abcdef01234567' });
    expect(user.json()).toMatchObject({ details: [{ field: 'assigneeId', message: 'unknown_user' }] });

    for (const bad of [
      { ...base, name: '   ' },
      { ...base, name: 'x'.repeat(121) },
      { ...base, durationMinutes: 0 },
      { ...base, durationMinutes: 1.5 },
      { ...base, date: '22-09-2026' },
      { ...base, requestId: 'short' },
      { durationMinutes: 10, date: '2026-09-22' },
    ]) {
      expect((await post(bad)).statusCode, JSON.stringify(bad)).toBe(400);
    }
    expect((await post({ ...base }, {})).statusCode).toBe(400);

    const notGenerated = await post({ ...base, date: '2026-12-01' });
    expect(notGenerated.statusCode).toBe(409);
    expect(notGenerated.json()).toMatchObject({ code: 'cycle_not_generated', date: '2026-12-01' });
  });
});

describe('POST /api/occurrences/one-off (done now)', () => {
  it('records one done document for the actor without touching any task', async () => {
    t.clock.set('2026-09-16T12:00:00.000Z');
    const { result, entries } = await expectAudited(
      t,
      () => post({ name: 'Zolder opruimen', roomId: woonkamer, durationMinutes: 90, date: '2026-09-16', done: true, requestId: key(1) }),
      { entity: 'occurrence', action: 'create', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(201);
    expect(result.json<Body>()).toMatchObject({
      taskId: null,
      status: 'done',
      recordedDone: true,
      requestId: key(1),
      statusBeforeCompletion: null,
      completedAt: '2026-09-16T12:00:00.000Z',
      completedBy: p1._id.toHexString(),
      assigneeId: p1._id.toHexString(),
    });
    expect(entries[0]!.meta).toEqual({ origin: 'adhoc', kind: 'one_off', recordedDone: true, requestId: key(1) });
    expect(entries[0]!.after).toMatchObject({ status: 'done', recordedDone: true });
  });

  it('records for an explicit person and requires today and a person', async () => {
    t.clock.set('2026-09-16T12:30:00.000Z');
    const explicit = await post({ name: 'Boekenkast', durationMinutes: 15, date: '2026-09-16', done: true, assigneeId: p2._id.toHexString() });
    expect(explicit.json<OccurrenceView>()).toMatchObject({ completedBy: p2._id.toHexString(), assigneeId: p2._id.toHexString() });

    const future = await captureWrites(t, () => post({ name: 'Morgen', durationMinutes: 15, date: '2026-09-17', done: true }));
    expect(future.result.statusCode).toBe(400);
    expect(future.result.json()).toMatchObject({ details: [{ field: 'date', message: 'done_requires_today' }] });
    const nobody = await captureWrites(t, () =>
      post({ name: 'Niemand', durationMinutes: 15, date: '2026-09-16', done: true, assigneeId: null }),
    );
    expect(nobody.result.json()).toMatchObject({ details: [{ field: 'assigneeId', message: 'done_requires_person' }] });
    expect(future.writes).toEqual([]);
    expect(nobody.writes).toEqual([]);
  });
});

describe('idempotent one-off creation', () => {
  it('replays the stored record for a repeated request: 200, one document, one audit entry, no writes', async () => {
    t.clock.set('2026-09-16T14:00:00.000Z');
    const body = { name: 'Fiets repareren', durationMinutes: 30, date: '2026-09-16', done: true, requestId: key(20) };
    const first = await expectAudited(t, () => post(body), { entity: 'occurrence', action: 'create', count: 1 });
    expect(first.result.statusCode, first.result.body).toBe(201);

    const replay = await captureWrites(t, () => post(body));
    expect(replay.result.statusCode, replay.result.body).toBe(200);
    expect(replay.result.json<OccurrenceView>()._id).toBe(first.result.json<OccurrenceView>()._id);
    expect(replay.writes).toEqual([]);
    expect(replay.auditInserts).toBe(0);
    expect(await findOccurrences(t.db, { requestId: key(20) })).toHaveLength(1);
  });

  it('rejects a reused key for a different request without writing', async () => {
    t.clock.set('2026-09-16T15:00:00.000Z');
    const body = { name: 'Schuur leegmaken', durationMinutes: 30, date: '2026-09-16', requestId: key(30) };
    expect((await post(body)).statusCode).toBe(201);

    const differentName = await captureWrites(t, () => post({ ...body, name: 'Andere klus' }));
    const differentDate = await post({ ...body, date: '2026-09-17' });
    const differentDone = await post({ ...body, done: true });
    const asExtra = await t.app.inject({
      method: 'POST',
      url: '/api/occurrences',
      headers: asProfile(p1),
      payload: { taskId: weekly, date: '2026-09-16', requestId: key(30) },
    });
    for (const res of [differentName.result, differentDate, differentDone, asExtra]) {
      expect(res.statusCode, res.body).toBe(409);
      expect(res.json()).toMatchObject({ code: 'idempotency_key_conflict' });
    }
    expect(differentName.writes).toEqual([]);
    expect(await findOccurrences(t.db, { requestId: key(30) })).toHaveLength(1);
  });
});

describe('retracting a recorded one-off task', () => {
  it('deletes it with an audited retract; a second retract is gone; uncomplete is refused', async () => {
    t.clock.set('2026-09-16T16:00:00.000Z');
    const created = (await post({ name: 'Tuinhuis verven', durationMinutes: 60, date: '2026-09-16', done: true, requestId: key(40) })).json<OccurrenceView>();

    const blocked = await captureWrites(t, () => patch(created._id, { action: 'uncomplete' }));
    expect(blocked.result.statusCode).toBe(409);
    expect(blocked.result.json()).toMatchObject({ code: 'retract_required' });
    expect(blocked.writes).toEqual([]);

    const { result, entries } = await expectAudited(t, () => retract(created._id), { entity: 'occurrence', action: 'delete', count: 1 });
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json()).toEqual({ retracted: true, id: created._id });
    expect(entries[0]!.meta).toEqual({ reason: 'retract' });
    expect(entries[0]!.before).toMatchObject({ taskId: null, taskNameSnapshot: 'Tuinhuis verven' });
    expect(await findOccurrences(t.db, { _id: new ObjectId(created._id) })).toHaveLength(0);

    const again = await captureWrites(t, () => retract(created._id));
    expect(again.result.statusCode).toBe(404);
    expect(again.writes).toEqual([]);
  });

  it('can complete and skip a planned one-off task like any occurrence, and retract refuses it', async () => {
    const planned = (await post({ name: 'Planklus', durationMinutes: 10, date: '2026-09-23' })).json<OccurrenceView>();
    const done = await patch(planned._id, { action: 'complete' });
    expect(done.statusCode, done.body).toBe(200);
    expect(done.json<OccurrenceView>()).toMatchObject({ taskId: null, status: 'done', recordedDone: false });
    expect((await patch(planned._id, { action: 'uncomplete' })).statusCode).toBe(200);
    expect((await retract(planned._id)).json()).toMatchObject({ code: 'not_retractable' });
    expect((await patch(planned._id, { action: 'skip' })).statusCode).toBe(200);
  });
});

describe('a one-off task has no central task record', () => {
  it('never appears in the task list, the due list or the AI proposal input', async () => {
    t.clock.set(DEFAULT_TEST_NOW);
    const name = 'Unieke eenmalige klus';
    expect((await post({ name, roomId: woonkamer, durationMinutes: 45, date: '2026-09-18' })).statusCode).toBe(201);

    const tasks = await get('/api/tasks');
    expect(tasks.statusCode).toBe(200);
    expect(tasks.body).not.toContain(name);
    expect(tasks.json<{ name: string }[]>().map((task) => task.name)).toEqual(['Afwas']);

    const due = await get('/api/due');
    expect(due.statusCode).toBe(200);
    expect(due.body).not.toContain(name);
    expect(due.json<{ taskId: string | null }[]>().every((item) => item.taskId !== null)).toBe(true);

    const proposal = await t.app.inject({ method: 'POST', url: '/api/ai/propose-plan', headers: asProfile(p1), payload: {} });
    expect(proposal.statusCode, proposal.body).toBe(200);
    expect(provider.requests.length).toBeGreaterThan(0);
    for (const request of provider.requests) {
      expect(request.user).not.toContain(name);
      expect(request.user).toContain('Afwas');
    }
  });
});

describe('activation of another plan', () => {
  it('lists open and done one-off tasks under the preserved ad-hoc group and keeps them', async () => {
    t.clock.set('2026-09-16T17:00:00.000Z');
    const open = (await post({ name: 'Open eenmalig', roomId: woonkamer, durationMinutes: 10, date: '2026-09-25' })).json<OccurrenceView>();
    const done = (
      await post({ name: 'Klaar eenmalig', durationMinutes: 10, date: '2026-09-16', done: true, requestId: key(60) })
    ).json<OccurrenceView>();

    const copy = await t.app.inject({
      method: 'POST',
      url: '/api/cycle-plans',
      headers: asProfile(p1),
      payload: { name: 'Zelfde slots', copyFromId: (await findActivePlan(t.db))!._id.toHexString() },
    });
    const planId = copy.json<{ _id: string }>()._id;
    const previewRequest = () =>
      t.app.inject({ method: 'GET', url: `/api/cycle-plans/${planId}/activation-preview`, headers: asProfile(p1) });
    const preview = (await previewRequest()).json<{
      previewToken: string;
      removed: { occurrenceId: string | null }[];
      preserved: { adhoc: { occurrenceId: string; taskId: string | null; taskName: string }[] };
    }>();
    const listed = preview.preserved.adhoc.filter((item) => item.taskId === null);
    expect(listed.map((item) => item.occurrenceId)).toEqual(expect.arrayContaining([open._id, done._id]));
    expect(listed.find((item) => item.occurrenceId === open._id)).toMatchObject({ taskName: 'Open eenmalig', taskId: null });
    expect(preview.removed.map((item) => item.occurrenceId)).not.toContain(open._id);

    const activated = await t.app.inject({
      method: 'POST',
      url: `/api/cycle-plans/${planId}/activate`,
      headers: asProfile(p1),
      payload: { previewToken: preview.previewToken },
    });
    expect(activated.statusCode, activated.body).toBe(200);
    expect((await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(p1) })).statusCode).toBe(200);

    const remaining = await oneOffs();
    expect(remaining.map((o) => o._id.toHexString())).toEqual(expect.arrayContaining([open._id, done._id]));
    expect(remaining.find((o) => o._id.toHexString() === open._id)).toMatchObject({ status: 'open', origin: 'adhoc' });
  });
});
