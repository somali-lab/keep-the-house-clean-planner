import { cycleStart, slotDate, toDayKey, type PromoteSuggestion } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { findOccurrences } from '../src/data/occurrences.ts';
import type { UserDoc } from '../src/data/users.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

// Anchor Monday 2026-09-14. The slot is week 2 (index 1), Tuesday, for Persoon 1:
// cycle 0 → Tue 22 Sep, cycle 1 → Tue 20 Oct, cycle 2 → Tue 17 Nov.
const ANCHOR = '2026-09-14';

let t: TestApp | undefined;

afterEach(async () => {
  await t?.close();
  t = undefined;
});

interface Ctx {
  t: TestApp;
  p1: UserDoc;
  p2: UserDoc;
  planId: string;
  taskId: string;
  get(): Promise<PromoteSuggestion[]>;
  move(cycleIndex: number, date: string, assigneeId?: string): Promise<string>;
}

async function setup(): Promise<Ctx> {
  const app = await createTestApp({ now: `${ANCHOR}T06:00:00.000Z` });
  t = app;
  const [p1, p2] = await seededUsers(app);
  const headers = asProfile(p1);
  const room = await seededRoom(app, 'Badkamer');
  const taskId = (
    await app.app.inject({
      method: 'POST',
      url: '/api/tasks',
      headers,
      payload: { name: 'Badkamer schoonmaken', roomId: room._id.toHexString(), intervalKey: '4wk', durationMinutes: 30 },
    })
  ).json<{ _id: string }>()._id;
  const planId = (await findActivePlan(app.db))!._id.toHexString();
  const put = await app.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${planId}/slots`,
    headers,
    payload: { slots: [{ taskId, weekIndex: 1, weekday: 2, assigneeId: p1._id.toHexString() }] },
  });
  expect(put.statusCode, put.body).toBe(200);
  expect((await app.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers })).statusCode).toBe(200);

  return {
    t: app,
    p1,
    p2,
    planId,
    taskId,
    async get() {
      const res = await app.app.inject({ method: 'GET', url: '/api/promote-suggestions' });
      expect(res.statusCode, res.body).toBe(200);
      return res.json<PromoteSuggestion[]>();
    },
    async move(cycleIndex, date, assigneeId) {
      const planned = slotDate(cycleStart(cycleIndex, ANCHOR), 1, 2);
      const [occ] = (await findOccurrences(app.db, { taskId: new ObjectId(taskId) })).filter(
        (o) => toDayKey(o.plannedDate) === planned,
      );
      expect(occ, `occurrence planned ${planned}`).toBeDefined();
      const id = occ!._id.toHexString();
      const res = await app.app.inject({ method: 'PATCH', url: `/api/occurrences/${id}`, headers, payload: { action: 'reschedule', date } });
      expect(res.statusCode, res.body).toBe(200);
      if (assigneeId) {
        const assign = await app.app.inject({ method: 'PATCH', url: `/api/occurrences/${id}`, headers, payload: { action: 'assign', assigneeId } });
        expect(assign.statusCode, assign.body).toBe(200);
      }
      return id;
    },
  };
}

describe('GET /api/promote-suggestions', () => {
  it('does not suggest after a single move', async () => {
    const c = await setup();
    await c.move(0, '2026-09-23'); // Tue → Wed
    expect(await c.get()).toEqual([]);
  });

  it('suggests the new weekday when two consecutive cycles were moved the same way', async () => {
    const c = await setup();
    const first = await c.move(0, '2026-09-23');
    const second = await c.move(1, '2026-10-21');
    expect(await c.get()).toEqual([
      {
        planId: c.planId,
        taskId: c.taskId,
        taskName: 'Badkamer schoonmaken',
        fromSlot: { weekIndex: 1, weekday: 2, assigneeId: c.p1._id.toHexString() },
        toWeekday: 3,
        evidence: [second, first],
      },
    ]);
  });

  it('does not suggest when the moves went to different weekdays', async () => {
    const c = await setup();
    await c.move(0, '2026-09-23'); // Wednesday
    await c.move(1, '2026-10-22'); // Thursday
    expect(await c.get()).toEqual([]);
  });

  it('includes the other person when every move also went to them', async () => {
    const c = await setup();
    const P2 = c.p2._id.toHexString();
    await c.move(0, '2026-09-24', P2);
    await c.move(1, '2026-10-22', P2);
    const [suggestion] = await c.get();
    expect(suggestion).toMatchObject({ toWeekday: 4, toAssigneeId: P2 });
  });

  it('keeps a dismissed suggestion away until there is newer evidence', async () => {
    const c = await setup();
    await c.move(0, '2026-09-23');
    await c.move(1, '2026-10-21');
    const [suggestion] = await c.get();

    const { result } = await expectAudited(
      c.t,
      () =>
        c.t.app.inject({
          method: 'POST',
          url: '/api/promote-suggestions/dismiss',
          headers: asProfile(c.p1),
          payload: {
            planId: suggestion!.planId,
            taskId: suggestion!.taskId,
            weekIndex: 1,
            weekday: 2,
            toWeekday: 3,
            toAssigneeId: null,
            lastEvidenceId: suggestion!.evidence[0],
          },
        }),
      { entity: 'settings', action: 'update', count: 1 },
    );
    expect(result.statusCode).toBe(200);
    expect(await c.get()).toEqual([]);

    // A cycle later the same move happens again.
    c.t.clock.set('2026-10-12T06:00:00.000Z');
    await c.t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(c.p1) });
    const third = await c.move(2, '2026-11-18');
    expect((await c.get()).map((s) => s.evidence[0])).toEqual([third]);
  });
});

describe('POST /api/promote-suggestions/apply', () => {
  it('fails with 422 when the new weekday is unavailable for the assignee, and changes nothing', async () => {
    const c = await setup();
    await c.move(0, '2026-09-23');
    await c.move(1, '2026-10-21');
    await c.t.app.inject({
      method: 'PATCH',
      url: `/api/users/${c.p1._id.toHexString()}`,
      headers: asProfile(c.p1),
      payload: { unavailableWeekdays: [3] },
    });
    const { result } = await expectAudited(
      c.t,
      () =>
        c.t.app.inject({
          method: 'POST',
          url: '/api/promote-suggestions/apply',
          headers: asProfile(c.p1),
          payload: { planId: c.planId, taskId: c.taskId, weekIndex: 1, weekday: 2, toWeekday: 3 },
        }),
      { entity: 'cyclePlan', action: 'update', count: 0 },
    );
    expect(result.statusCode).toBe(422);
    expect(result.json<{ code: string; details: { errors: { code: string }[] } }>()).toMatchObject({
      code: 'invalid_plan',
      details: { errors: [expect.objectContaining({ code: 'assignee_unavailable' })] },
    });
    expect((await findActivePlan(c.t.db))!.slots[0]).toMatchObject({ weekday: 2 });
  });

  it('updates the slot, audits it with promotedFrom, and the suggestion disappears', async () => {
    const c = await setup();
    await c.move(0, '2026-09-23');
    await c.move(1, '2026-10-21');
    const { result, entries } = await expectAudited(
      c.t,
      () =>
        c.t.app.inject({
          method: 'POST',
          url: '/api/promote-suggestions/apply',
          headers: asProfile(c.p1),
          payload: { planId: c.planId, taskId: c.taskId, weekIndex: 1, weekday: 2, toWeekday: 3 },
        }),
      { entity: 'cyclePlan', action: 'update', source: 'ui', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json<{ plan: { slots: { weekday: number }[] } }>().plan.slots).toEqual([
      expect.objectContaining({ weekIndex: 1, weekday: 3 }),
    ]);
    expect(entries[0]!.meta).toMatchObject({ promotedFrom: { weekIndex: 1, weekday: 2 }, toWeekday: 3 });
    expect(await c.get()).toEqual([]);
  });

  it('only changes the active plan and an existing slot', async () => {
    const c = await setup();
    const headers = asProfile(c.p1);
    const wrongPlan = await c.t.app.inject({
      method: 'POST',
      url: '/api/promote-suggestions/apply',
      headers,
      payload: { planId: '0123456789abcdef01234567', taskId: c.taskId, weekIndex: 1, weekday: 2, toWeekday: 3 },
    });
    expect(wrongPlan.statusCode).toBe(409);
    const noSlot = await c.t.app.inject({
      method: 'POST',
      url: '/api/promote-suggestions/apply',
      headers,
      payload: { planId: c.planId, taskId: c.taskId, weekIndex: 2, weekday: 2, toWeekday: 3 },
    });
    expect(noSlot.statusCode).toBe(404);
    const invalid = await c.t.app.inject({ method: 'POST', url: '/api/promote-suggestions/apply', headers, payload: { planId: c.planId } });
    expect(invalid.statusCode).toBe(400);
  });
});
