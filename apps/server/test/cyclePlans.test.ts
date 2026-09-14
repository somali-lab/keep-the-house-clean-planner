import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { COLLECTIONS } from '../src/data/db.ts';
import type { UserDoc } from '../src/data/users.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
let weekly: string; // 1w, 30 min
let twice: string; // 2w, 10 min
let active: PlanJson;

interface SlotJson {
  taskId: string;
  weekIndex: number;
  weekday: number;
  assigneeId: string | null;
  sortOrder: number;
}

interface PlanJson {
  _id: string;
  name: string;
  active: boolean;
  slots: SlotJson[];
  weekThemes: string[];
  draft: boolean;
}

const headers = () => asProfile(p1);

async function createTask(name: string, intervalKey: string, durationMinutes: number): Promise<string> {
  const room = await seededRoom(t, 'Badkamer');
  const res = await t.app.inject({
    method: 'POST',
    url: '/api/tasks',
    headers: headers(),
    payload: { name, roomId: room._id.toHexString(), intervalKey, durationMinutes },
  });
  return res.json<{ _id: string }>()._id;
}

function putSlots(planId: string, slots: Partial<SlotJson>[]) {
  return t.app.inject({ method: 'PUT', url: `/api/cycle-plans/${planId}/slots`, headers: headers(), payload: { slots } });
}

beforeAll(async () => {
  t = await createTestApp();
  [p1, p2] = await seededUsers(t);
  // Persoon 2 is not available on Tuesday
  await t.app.inject({
    method: 'PATCH',
    url: `/api/users/${p2._id.toHexString()}`,
    headers: headers(),
    payload: { unavailableWeekdays: [2] },
  });
  weekly = await createTask('Badkamer schoonmaken', '1w', 30);
  twice = await createTask('Wastafel poetsen', '2w', 10);
  active = (await t.app.inject({ method: 'GET', url: '/api/cycle-plans/active' })).json<PlanJson>();
});

afterAll(async () => {
  await t.close();
});

describe('cycle plans: read and create', () => {
  it('starts with an empty active plan "Standaard"', async () => {
    expect(active).toMatchObject({ name: 'Standaard', active: true, slots: [], weekThemes: ['', '', '', ''] });
    const list = await t.app.inject({ method: 'GET', url: '/api/cycle-plans' });
    expect(list.json<PlanJson[]>().map((p) => p.name)).toEqual(['Standaard']);
    const one = await t.app.inject({ method: 'GET', url: `/api/cycle-plans/${active._id}` });
    expect(one.json<PlanJson>()._id).toBe(active._id);
  });

  it('returns 404 for an unknown plan', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/api/cycle-plans/0123456789abcdef01234567' });
    expect(res.statusCode).toBe(404);
  });

  it('creates an inactive copy with slots and themes, audited with copiedFrom', async () => {
    await putSlots(active._id, [{ taskId: weekly, weekIndex: 0, weekday: 1, assigneeId: p1._id.toHexString() }]);
    const { result, entries } = await expectAudited(
      t,
      () =>
        t.app.inject({
          method: 'POST',
          url: '/api/cycle-plans',
          headers: headers(),
          payload: { name: 'Experiment', copyFromId: active._id },
        }),
      { entity: 'cyclePlan', action: 'create', source: 'ui', count: 1 },
    );
    expect(result.statusCode).toBe(201);
    const copy = result.json<PlanJson>();
    expect(copy).toMatchObject({ name: 'Experiment', active: false, draft: false });
    expect(copy.slots).toEqual([{ taskId: weekly, weekIndex: 0, weekday: 1, assigneeId: p1._id.toHexString(), sortOrder: 0 }]);
    expect(entries[0]!.meta).toEqual({ copiedFrom: expect.anything() });
    expect(String(entries[0]!.meta?.copiedFrom)).toBe(active._id);
  });

  it('returns 404 when copying an unknown plan and 400 without a name', async () => {
    const unknown = await t.app.inject({
      method: 'POST',
      url: '/api/cycle-plans',
      headers: headers(),
      payload: { name: 'X', copyFromId: '0123456789abcdef01234567' },
    });
    expect(unknown.statusCode).toBe(404);
    const noName = await t.app.inject({ method: 'POST', url: '/api/cycle-plans', headers: headers(), payload: {} });
    expect(noName.statusCode).toBe(400);
  });

  it('renames and sets week themes with an audited diff', async () => {
    const created = await t.app.inject({ method: 'POST', url: '/api/cycle-plans', headers: headers(), payload: { name: 'Leeg' } });
    const id = created.json<PlanJson>()._id;
    const { result, entries } = await expectAudited(
      t,
      () =>
        t.app.inject({
          method: 'PATCH',
          url: `/api/cycle-plans/${id}`,
          headers: headers(),
          payload: { name: 'Zomer', weekThemes: ['Keuken', '', 'Ramen', ''] },
        }),
      { entity: 'cyclePlan', action: 'update', count: 1 },
    );
    expect(result.statusCode).toBe(200);
    expect(entries[0]!.before).toEqual({ name: 'Leeg', weekThemes: ['', '', '', ''] });
    expect(entries[0]!.after).toEqual({ name: 'Zomer', weekThemes: ['Keuken', '', 'Ramen', ''] });
    const badThemes = await t.app.inject({
      method: 'PATCH',
      url: `/api/cycle-plans/${id}`,
      headers: headers(),
      payload: { weekThemes: ['a', 'b'] },
    });
    expect(badThemes.statusCode).toBe(400);
  });

  it('deletes a non-active copy but never deletes the original default plan', async () => {
    const blocked = await t.app.inject({
      method: 'DELETE',
      url: `/api/cycle-plans/${active._id}`,
      headers: headers(),
    });
    expect(blocked.statusCode).toBe(409);
    expect(blocked.json<{ code: string }>().code).toBe('default_plan');

    const created = await t.app.inject({
      method: 'POST',
      url: '/api/cycle-plans',
      headers: headers(),
      payload: { name: 'Tijdelijk' },
    });
    const id = created.json<PlanJson>()._id;
    const { result, entries } = await expectAudited(
      t,
      () =>
        t.app.inject({ method: 'DELETE', url: `/api/cycle-plans/${id}`, headers: headers() }),
      { entity: 'cyclePlan', action: 'delete', count: 1 },
    );
    expect(result.statusCode).toBe(200);
    expect(result.json()).toEqual({ deleted: true });
    expect(entries[0]!.before).toMatchObject({ name: 'Tijdelijk', active: false });
    expect((await t.app.inject({ method: 'GET', url: `/api/cycle-plans/${id}` })).statusCode).toBe(404);
  });
});

describe('PUT /api/cycle-plans/:id/slots', () => {
  let planId: string;

  beforeAll(async () => {
    const created = await t.app.inject({ method: 'POST', url: '/api/cycle-plans', headers: headers(), payload: { name: 'Slots' } });
    planId = created.json<PlanJson>()._id;
  });

  it('saves a valid plan and returns warnings and summary (warning path)', async () => {
    const { result, entries } = await expectAudited(
      t,
      () =>
        putSlots(planId, [
          { taskId: weekly, weekIndex: 0, weekday: 1, assigneeId: p1._id.toHexString() },
          { taskId: twice, weekIndex: 0, weekday: 3, assigneeId: null },
        ]),
      { entity: 'cyclePlan', action: 'update', source: 'ui', count: 1 },
    );
    expect(result.statusCode).toBe(200);
    const body = result.json<{
      plan: PlanJson;
      warnings: { code: string; taskId?: string; placed?: number; required?: number }[];
      summary: { tasks: { taskId: string; placed: number; required: number | null }[] };
    }>();
    expect(body.plan.slots).toHaveLength(2);
    expect(body.warnings).toEqual(
      expect.arrayContaining([
        { code: 'interval_mismatch', taskId: weekly, placed: 1, required: 4 },
        { code: 'interval_mismatch', taskId: twice, placed: 1, required: 8 },
      ]),
    );
    expect(body.summary.tasks).toEqual(
      expect.arrayContaining([{ taskId: weekly, placed: 1, required: 4 }]),
    );
    // audit: only added slots, nothing removed
    expect(entries[0]!.before).toEqual({ slots: [] });
    expect((entries[0]!.after.slots as unknown[]).length).toBe(2);
  });

  it('audits only added, removed and changed slots', async () => {
    const { entries } = await expectAudited(
      t,
      () =>
        putSlots(planId, [
          // changed: assignee p1 → p2 (Monday is fine for p2)
          { taskId: weekly, weekIndex: 0, weekday: 1, assigneeId: p2._id.toHexString() },
          // removed: twice on week 0 Wednesday; added: twice on week 1 Friday
          { taskId: twice, weekIndex: 1, weekday: 5, assigneeId: null },
        ]),
      { entity: 'cyclePlan', action: 'update', count: 1 },
    );
    const before = entries[0]!.before.slots as { weekIndex: number; weekday: number; assigneeId: unknown }[];
    const after = entries[0]!.after.slots as { weekIndex: number; weekday: number; assigneeId: unknown }[];
    expect(before.map((s) => [s.weekIndex, s.weekday])).toEqual([
      [0, 3], // removed
      [0, 1], // changed (before)
    ]);
    expect(String(before[1]!.assigneeId)).toBe(p1._id.toHexString());
    expect(after.map((s) => [s.weekIndex, s.weekday])).toEqual([
      [1, 5], // added
      [0, 1], // changed (after)
    ]);
    expect(String(after[1]!.assigneeId)).toBe(p2._id.toHexString());
  });

  it('returns 422 with details for hard errors and leaves the plan untouched', async () => {
    const before = await t.db.collection(COLLECTIONS.cyclePlans).findOne({ name: 'Slots' });
    const { result } = await expectAudited(
      t,
      () =>
        putSlots(planId, [
          // Persoon 2 is unavailable on Tuesday
          { taskId: weekly, weekIndex: 2, weekday: 2, assigneeId: p2._id.toHexString() },
        ]),
      { entity: 'cyclePlan', action: 'update', count: 0 },
    );
    expect(result.statusCode).toBe(422);
    const body = result.json<{ code: string; details: { errors: { code: string; slotIndex: number }[] } }>();
    expect(body.code).toBe('invalid_plan');
    expect(body.details.errors).toEqual([expect.objectContaining({ code: 'assignee_unavailable', slotIndex: 0 })]);
    const after = await t.db.collection(COLLECTIONS.cyclePlans).findOne({ name: 'Slots' });
    expect(after?.slots).toEqual(before?.slots);
  });

  it('rejects the same task twice on one day with 422', async () => {
    const res = await putSlots(planId, [
      { taskId: twice, weekIndex: 3, weekday: 4, assigneeId: p1._id.toHexString() },
      { taskId: twice, weekIndex: 3, weekday: 4, assigneeId: p2._id.toHexString() },
    ]);
    expect(res.statusCode).toBe(422);
    expect(res.json<{ details: { errors: { code: string }[] } }>().details.errors.map((e) => e.code)).toEqual([
      'duplicate_task_day',
    ]);
  });

  it('does not audit an identical PUT', async () => {
    const current = (await t.app.inject({ method: 'GET', url: `/api/cycle-plans/${planId}` })).json<PlanJson>();
    const { result } = await expectAudited(t, () => putSlots(planId, current.slots), {
      entity: 'cyclePlan',
      action: 'update',
      count: 0,
    });
    expect(result.statusCode).toBe(200);
  });

  it('validates the body and the plan id', async () => {
    expect((await putSlots(planId, [{ taskId: 'nope', weekIndex: 0, weekday: 1, assigneeId: null }])).statusCode).toBe(400);
    expect((await putSlots('0123456789abcdef01234567', [])).statusCode).toBe(404);
  });
});
