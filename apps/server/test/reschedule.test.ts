import type { ApiWarning, OccurrenceView } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { listCycles } from '../src/data/cycles.ts';
import { findOccurrenceById, findOccurrences } from '../src/data/occurrences.ts';
import type { UserDoc } from '../src/data/users.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

// Generated on Monday 14 Sep 2026: cycle 0 = 14 Sep–11 Oct, cycle 1 = 12 Oct–8 Nov.
let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
let weekly: string;

type PatchJson = OccurrenceView & { warnings: ApiWarning[] };

const patch = (id: string, payload: Record<string, unknown>) =>
  t.app.inject({ method: 'PATCH', url: `/api/occurrences/${id}`, headers: asProfile(p1), payload });

async function occurrenceOn(date: string): Promise<string> {
  const docs = await findOccurrences(t.db, { taskId: new ObjectId(weekly) });
  const res = await t.app.inject({ method: 'GET', url: `/api/occurrences?from=${date}&to=${date}` });
  const view = res.json<OccurrenceView[]>().find((o) => o.taskId === weekly);
  if (!view || !docs.some((d) => d._id.toHexString() === view._id)) throw new Error(`no occurrence on ${date}`);
  return view._id;
}

beforeAll(async () => {
  t = await createTestApp({ now: '2026-09-14T06:00:00.000Z' });
  [p1, p2] = await seededUsers(t);
  // Persoon 2 cannot do Tuesdays.
  await t.app.inject({ method: 'PATCH', url: `/api/users/${p2._id.toHexString()}`, headers: asProfile(p1), payload: { unavailableWeekdays: [2] } });
  const room = await seededRoom(t, 'Badkamer');
  weekly = (
    await t.app.inject({
      method: 'POST',
      url: '/api/tasks',
      headers: asProfile(p1),
      payload: { name: 'Badkamer schoonmaken', roomId: room._id.toHexString(), intervalKey: '1w', durationMinutes: 30 },
    })
  ).json<{ _id: string }>()._id;
  const planId = (await findActivePlan(t.db))!._id.toHexString();
  const P2 = p2._id.toHexString();
  await t.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${planId}/slots`,
    headers: asProfile(p1),
    payload: {
      slots: [
        ...[0, 1, 2].map((w) => ({ taskId: weekly, weekIndex: w, weekday: 1, assigneeId: P2 })),
        { taskId: weekly, weekIndex: 3, weekday: 0, assigneeId: P2 }, // Sunday 11 Oct, last day of cycle 0
      ],
    },
  });
  expect((await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(p1) })).statusCode).toBe(200);
});

afterAll(async () => {
  await t.close();
});

describe('PATCH reschedule', () => {
  it('moves the occurrence, keeps plannedDate and audits from/to day keys', async () => {
    const id = await occurrenceOn('2026-09-14');
    const { result, entries } = await expectAudited(t, () => patch(id, { action: 'reschedule', date: '2026-09-16' }), {
      entity: 'occurrence',
      action: 'reschedule',
      source: 'ui',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json<PatchJson>()).toMatchObject({
      date: '2026-09-16',
      plannedDate: '2026-09-14',
      movedFrom: '2026-09-14',
      warnings: [],
    });
    expect(entries[0]!.meta).toMatchObject({
      from: '2026-09-14',
      to: '2026-09-16',
      occurrence: {
        taskNameSnapshot: 'Badkamer schoonmaken',
        roomNameSnapshot: 'Badkamer',
        date: expect.any(Date),
      },
    });
    expect(entries[0]!.before.date).toEqual(new Date('2026-09-13T22:00:00Z'));
    expect(entries[0]!.after.date).toEqual(new Date('2026-09-15T22:00:00Z'));
  });

  it('allows a day the assignee is unavailable, with a warning', async () => {
    const id = await occurrenceOn('2026-09-21');
    const res = await patch(id, { action: 'reschedule', date: '2026-09-22' }); // Tuesday
    expect(res.statusCode).toBe(200);
    const body = res.json<PatchJson>();
    expect(body.date).toBe('2026-09-22');
    expect(body.warnings).toEqual([
      expect.objectContaining({ code: 'assignee_unavailable', details: { userId: p2._id.toHexString(), weekday: 2 } }),
    ]);
  });

  it('moves the occurrence to the other cycle when the new date is in it', async () => {
    const id = await occurrenceOn('2026-10-11');
    const [cycle0, cycle1] = await listCycles(t.db);
    expect((await findOccurrenceById(t.db, new ObjectId(id)))?.cycleId).toEqual(cycle0!._id);
    const res = await patch(id, { action: 'reschedule', date: '2026-10-12' });
    expect(res.statusCode, res.body).toBe(200);
    const stored = await findOccurrenceById(t.db, new ObjectId(id));
    expect(stored?.cycleId).toEqual(cycle1!._id);
    expect(res.json<PatchJson>()).toMatchObject({ date: '2026-10-12', plannedDate: '2026-10-11' });
  });

  it('can move back to the planned day, which clears movedFrom', async () => {
    const id = await occurrenceOn('2026-09-16');
    const res = await patch(id, { action: 'reschedule', date: '2026-09-14' });
    expect(res.json<PatchJson>()).toMatchObject({ date: '2026-09-14', movedFrom: null });
  });

  it('does not write or audit a move to the same day', async () => {
    const id = await occurrenceOn('2026-09-14');
    const { result } = await expectAudited(t, () => patch(id, { action: 'reschedule', date: '2026-09-14' }), {
      entity: 'occurrence',
      action: 'reschedule',
      count: 0,
    });
    expect(result.statusCode).toBe(200);
  });

  it('refuses days that are not generated, finished occurrences and bad dates', async () => {
    const id = await occurrenceOn('2026-09-14');
    const outside = await patch(id, { action: 'reschedule', date: '2026-12-01' });
    expect(outside.statusCode).toBe(409);
    expect(outside.json()).toMatchObject({ code: 'cycle_not_generated' });
    expect((await patch(id, { action: 'reschedule', date: '1-12-2026' })).statusCode).toBe(400);

    await patch(id, { action: 'complete' });
    const done = await patch(id, { action: 'reschedule', date: '2026-09-15' });
    expect(done.statusCode).toBe(409);
    expect(done.json()).toMatchObject({ code: 'invalid_transition' });
  });
});

describe('PATCH assign', () => {
  it('reassigns with an audit entry, and warns when the new assignee is unavailable that day', async () => {
    const id = await occurrenceOn('2026-09-28'); // Monday, assigned to Persoon 2
    const { result, entries } = await expectAudited(t, () => patch(id, { action: 'assign', assigneeId: p1._id.toHexString() }), {
      entity: 'occurrence',
      action: 'assign',
      count: 1,
    });
    expect(result.json<PatchJson>()).toMatchObject({ assigneeId: p1._id.toHexString(), warnings: [] });
    expect(entries[0]!.before).toEqual({ assigneeId: p2._id });
    expect(entries[0]!.after).toEqual({ assigneeId: p1._id });

    // move it to a Tuesday, then give it back to Persoon 2
    await patch(id, { action: 'reschedule', date: '2026-09-29' });
    const back = await patch(id, { action: 'assign', assigneeId: p2._id.toHexString() });
    expect(back.json<PatchJson>().warnings).toEqual([expect.objectContaining({ code: 'assignee_unavailable' })]);
  });

  it('accepts "wie dan ook" and rejects unknown users', async () => {
    const id = await occurrenceOn('2026-10-19'); // Monday, week 1 of cycle 1
    const anyone = await patch(id, { action: 'assign', assigneeId: null });
    expect(anyone.json<PatchJson>().assigneeId).toBeNull();
    const unknown = await patch(id, { action: 'assign', assigneeId: '0123456789abcdef01234567' });
    expect(unknown.statusCode).toBe(400);
    expect(unknown.json()).toMatchObject({ details: [{ field: 'assigneeId', message: 'unknown_user' }] });
  });
});
