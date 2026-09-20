import { toDayKey } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import cron from 'node-cron';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { listCycles } from '../src/data/cycles.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import {
  countOccurrences,
  findOccurrences,
  insertOccurrencesIdempotent,
  updateOccurrence,
  type OccurrenceDoc,
} from '../src/data/occurrences.ts';
import type { UserDoc } from '../src/data/users.ts';
import { startScheduler } from '../src/jobs/nightly.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

// Anchor 2026-09-14 (Mon). Cycle 0: 14 Sep – 11 Oct. Cycle 1: 12 Oct – 8 Nov (contains DST end, 25 Oct).
const MONDAY_MORNING = '2026-09-14T06:00:00.000Z';

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
  task(name: string, intervalKey: string, durationMinutes?: number): Promise<string>;
  putSlots(
    planId: string,
    slots: { taskId: string; weekIndex: number; weekday: number; assigneeId?: string | null }[],
    sync?: boolean,
  ): Promise<void>;
  nightly(): Promise<{
    runId: string;
    removed: number;
    generated: { cycleIndex: number; inserted: number; skipped: number }[];
  }>;
}

async function setup(now = MONDAY_MORNING): Promise<Ctx> {
  t = await createTestApp({ now });
  const app = t;
  const [p1, p2] = await seededUsers(app);
  const room = await seededRoom(app, 'Badkamer');
  const headers = asProfile(p1);
  const planId = (await findActivePlan(app.db))!._id.toHexString();
  return {
    t: app,
    p1,
    p2,
    planId,
    async task(name, intervalKey, durationMinutes = 10) {
      const res = await app.app.inject({
        method: 'POST',
        url: '/api/tasks',
        headers,
        payload: { name, roomId: room._id.toHexString(), intervalKey, durationMinutes },
      });
      expect(res.statusCode).toBe(201);
      return res.json<{ _id: string }>()._id;
    },
    async putSlots(id, slots, sync = false) {
      const res = await app.app.inject({
        method: 'PUT',
        url: `/api/cycle-plans/${id}/slots${sync ? '?sync=true' : ''}`,
        headers,
        payload: { slots: slots.map((s) => ({ assigneeId: null, ...s })) },
      });
      expect(res.statusCode, res.body).toBe(200);
    },
    async nightly() {
      const res = await app.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers });
      expect(res.statusCode, res.body).toBe(200);
      return res.json();
    },
  };
}

const dayKeys = (docs: OccurrenceDoc[]) => docs.map((d) => toDayKey(d.date)).sort();

describe('generateCycle via nightly job', () => {
  it('(a) is idempotent: a second run inserts and audits nothing', async () => {
    const c = await setup();
    const weekly = await c.task('Badkamer', '1w');
    await c.putSlots(
      c.planId,
      [0, 1, 2, 3].map((w) => ({ taskId: weekly, weekIndex: w, weekday: 3 })),
    );

    const first = await c.nightly();
    expect(first.generated.map((g) => [g.cycleIndex, g.inserted])).toEqual([
      [0, 4],
      [1, 4],
    ]);
    const count = await countOccurrences(c.t.db);
    expect(count).toBe(8);
    expect(await listCycles(c.t.db)).toHaveLength(2);

    const { result } = await expectAudited(c.t, () => c.nightly(), {
      entity: 'occurrence',
      action: 'create',
      count: 0,
    });
    expect(result.generated.map((g) => [g.inserted, g.skipped])).toEqual([
      [0, 4],
      [0, 4],
    ]);
    expect(await countOccurrences(c.t.db)).toBe(count);
    expect(await listCycles(c.t.db)).toHaveLength(2);
  });

  it('audits each generated occurrence with the run id', async () => {
    const c = await setup();
    const weekly = await c.task('Badkamer', '1w');
    await c.putSlots(c.planId, [{ taskId: weekly, weekIndex: 1, weekday: 1 }]);
    const { result, entries } = await expectAudited(c.t, () => c.nightly(), {
      entity: 'occurrence',
      action: 'create',
      count: 2,
    });
    expect(entries.every((e) => e.meta?.runId === result.runId)).toBe(true);
    expect(entries[0]!.after).toMatchObject({
      status: 'open',
      origin: 'generated',
      taskNameSnapshot: 'Badkamer',
      roomNameSnapshot: 'Badkamer',
    });
    const cycleAudit = await c.t.db
      .collection(COLLECTIONS.auditLog)
      .countDocuments({ entity: 'cycle', action: 'create' });
    expect(cycleAudit).toBe(2);
  });

  it('snapshots name, duration, assignee and plan; plannedDate equals date', async () => {
    const c = await setup();
    const weekly = await c.task('Badkamer', '1w', 30);
    await c.putSlots(c.planId, [
      { taskId: weekly, weekIndex: 2, weekday: 5, assigneeId: c.p2._id.toHexString() },
    ]);
    await c.nightly();
    const [occ] = await findOccurrences(c.t.db, {});
    expect(occ).toMatchObject({
      taskNameSnapshot: 'Badkamer',
      roomNameSnapshot: 'Badkamer',
      roomIdSnapshot: expect.any(ObjectId),
      durationMinutesSnapshot: 30,
      assigneeId: c.p2._id,
      planId: new ObjectId(c.planId),
      status: 'open',
    });
    expect(occ!.plannedDate).toEqual(occ!.date);
    expect(toDayKey(occ!.date)).toBe('2026-10-02');
  });

  it('(b) leaves vacation days empty', async () => {
    const c = await setup();
    const daily = await c.task('Afwas', 'daily', 15);
    await c.putSlots(
      c.planId,
      [0, 1].flatMap((w) =>
        [1, 2, 3, 4, 5, 6, 0].map((d) => ({ taskId: daily, weekIndex: w, weekday: d })),
      ),
    );
    await c.t.app.inject({
      method: 'PATCH',
      url: '/api/settings',
      headers: asProfile(c.p1),
      payload: {
        vacationRanges: [
          { from: '2026-09-17', to: '2026-09-20' },
          { from: '2026-10-12', to: '2026-10-18' },
        ],
      },
    });
    await c.nightly();
    const keys = dayKeys(await findOccurrences(c.t.db, {}));
    for (const vacation of [
      '2026-09-17',
      '2026-09-18',
      '2026-09-19',
      '2026-09-20',
      '2026-10-12',
      '2026-10-18',
    ]) {
      expect(keys).not.toContain(vacation);
    }
    expect(keys).toContain('2026-09-16');
    expect(keys).toContain('2026-09-21');
    expect(keys).toContain('2026-10-19');
    expect(keys).toHaveLength(28 - 4 - 7);
  });

  it('skips inactive tasks and days before today', async () => {
    const c = await setup('2026-09-16T08:00:00.000Z'); // Wednesday
    const weekly = await c.task('Badkamer', '1w');
    const old = await c.task('Oud', '1w');
    await c.putSlots(c.planId, [
      { taskId: weekly, weekIndex: 0, weekday: 1 }, // Mon 14 Sep: before today
      { taskId: weekly, weekIndex: 0, weekday: 3 }, // Wed 16 Sep: today
      { taskId: old, weekIndex: 0, weekday: 4 },
    ]);
    await c.t.app.inject({
      method: 'PATCH',
      url: `/api/tasks/${old}`,
      headers: asProfile(c.p1),
      payload: { active: false },
    });
    await c.nightly();
    // cycle 1 starts at local midnight of 12 Oct = 11 Oct 22:00 UTC
    const cycle0 = await findOccurrences(c.t.db, {
      date: { $lt: new Date('2026-10-11T22:00:00Z') },
    });
    expect(dayKeys(cycle0)).toEqual(['2026-09-16']);
  });

  it('(d) produces correct local dates across the DST change', async () => {
    const c = await setup();
    const weekly = await c.task('Badkamer', '1w');
    // cycle 1: week 1 Sunday = 25 Oct (DST ends), week 2 Monday = 26 Oct
    await c.putSlots(c.planId, [
      { taskId: weekly, weekIndex: 1, weekday: 0 },
      { taskId: weekly, weekIndex: 2, weekday: 1 },
    ]);
    await c.nightly();
    const cycle1 = await findOccurrences(c.t.db, {
      date: { $gte: new Date('2026-10-11T22:00:00Z') },
    });
    expect(cycle1.map((o) => o.date.toISOString())).toEqual([
      '2026-10-24T22:00:00.000Z',
      '2026-10-25T23:00:00.000Z',
    ]);
    expect(dayKeys(cycle1)).toEqual(['2026-10-25', '2026-10-26']);
  });

  it('(e) a 2w task with 8 slots yields 8 occurrences per cycle', async () => {
    const c = await setup();
    const twice = await c.task('Wastafel', '2w');
    await c.putSlots(
      c.planId,
      [0, 1, 2, 3].flatMap((w) => [
        { taskId: twice, weekIndex: w, weekday: 2 },
        { taskId: twice, weekIndex: w, weekday: 5 },
      ]),
    );
    const result = await c.nightly();
    expect(result.generated.map((g) => g.inserted)).toEqual([8, 8]);
  });
});

describe('editing the active plan', () => {
  it('repairs future occurrences generated before the cycle anchor changed', async () => {
    const c = await setup('2026-09-20T08:00:00.000Z');
    const water = await c.task('Waterbak', '1w', 3);
    await c.putSlots(
      c.planId,
      [0, 1, 2, 3].map((weekIndex) => ({
        taskId: water,
        weekIndex,
        weekday: 3,
        assigneeId: c.p1._id.toHexString(),
      })),
    );
    await c.nightly();

    const settings = await c.t.app.inject({
      method: 'PATCH',
      url: '/api/settings',
      headers: asProfile(c.p1),
      payload: { cycleAnchorDate: '2026-09-21' },
    });
    expect(settings.statusCode, settings.body).toBe(200);
    await c.putSlots(
      c.planId,
      [0, 1, 2, 3].flatMap((weekIndex) => [1, 3, 5].map((weekday) => ({
        taskId: water,
        weekIndex,
        weekday,
        assigneeId: c.p2._id.toHexString(),
      }))),
    );

    const result = await c.nightly();

    expect(result.removed).toBe(7);
    const occurrences = await findOccurrences(c.t.db, { taskId: new ObjectId(water) });
    expect(dayKeys(occurrences)).toEqual([
      '2026-09-21',
      '2026-09-23',
      '2026-09-25',
      '2026-09-28',
      '2026-09-30',
      '2026-10-02',
      '2026-10-05',
      '2026-10-07',
      '2026-10-09',
      '2026-10-12',
      '2026-10-14',
      '2026-10-16',
    ]);
    expect(occurrences.every((occurrence) => occurrence.assigneeId?.equals(c.p2._id))).toBe(true);
    expect((await listCycles(c.t.db)).map((cycle) => [cycle.index, cycle.startDate, cycle.endDate])).toEqual([
      [-1, '2026-08-24', '2026-09-20'],
      [0, '2026-09-21', '2026-10-18'],
      [1, '2026-10-19', '2026-11-15'],
    ]);
  });

  it('repairs stale upcoming occurrences during the nightly run', async () => {
    const c = await setup();
    const weekly = await c.task('Badkamer', '1w', 30);
    await c.putSlots(
      c.planId,
      [0, 1, 2, 3].map((weekIndex) => ({
        taskId: weekly,
        weekIndex,
        weekday: 1,
        assigneeId: c.p1._id.toHexString(),
      })),
    );
    await c.nightly();

    await c.putSlots(
      c.planId,
      [0, 1, 2, 3].map((weekIndex) => ({
        taskId: weekly,
        weekIndex,
        weekday: 4,
        assigneeId: c.p2._id.toHexString(),
      })),
    );
    await c.nightly();

    const occurrences = await findOccurrences(c.t.db, { taskId: new ObjectId(weekly) });
    expect(dayKeys(occurrences)).toEqual([
      '2026-09-17',
      '2026-09-24',
      '2026-10-01',
      '2026-10-08',
      '2026-10-15',
      '2026-10-22',
      '2026-10-29',
      '2026-11-05',
    ]);
    expect(occurrences.every((occurrence) => occurrence.assigneeId?.equals(c.p2._id))).toBe(true);
    const deletions = await c.t.db
      .collection(COLLECTIONS.auditLog)
      .find({ entity: 'occurrence', action: 'delete', 'meta.reason': 'nightly_reconciliation' })
      .toArray();
    expect(deletions).toHaveLength(8);
  });

  it('synchronizes upcoming occurrences when requested by the planner', async () => {
    const c = await setup();
    const weekly = await c.task('Badkamer', '1w', 30);
    await c.putSlots(
      c.planId,
      [0, 1, 2, 3].map((weekIndex) => ({
        taskId: weekly,
        weekIndex,
        weekday: 1,
        assigneeId: c.p1._id.toHexString(),
      })),
    );
    await c.nightly();

    await c.putSlots(
      c.planId,
      [0, 1, 2, 3].map((weekIndex) => ({
        taskId: weekly,
        weekIndex,
        weekday: 4,
        assigneeId: c.p2._id.toHexString(),
      })),
      true,
    );

    const occurrences = await findOccurrences(c.t.db, { taskId: new ObjectId(weekly) });
    expect(dayKeys(occurrences)).toEqual([
      '2026-09-17',
      '2026-09-24',
      '2026-10-01',
      '2026-10-08',
      '2026-10-15',
      '2026-10-22',
      '2026-10-29',
      '2026-11-05',
    ]);
    expect(occurrences.every((occurrence) => occurrence.assigneeId?.equals(c.p2._id))).toBe(true);
    const deletions = await c.t.db
      .collection(COLLECTIONS.auditLog)
      .find({ entity: 'occurrence', action: 'delete', 'meta.reason': 'plan_update' })
      .toArray();
    expect(deletions).toHaveLength(8);
  });
});

describe('POST /api/cycle-plans/:id/activate', () => {
  it('(c) mid-cycle activation keeps done, skipped, dragged, past and ad-hoc occurrences and replaces the rest', async () => {
    const c = await setup(); // Monday 14 Sep
    const weekly = await c.task('Badkamer', '1w', 30);
    const twice = await c.task('Wastafel', '2w', 10);
    await c.putSlots(c.planId, [
      ...[0, 1, 2, 3].map((w) => ({
        taskId: weekly,
        weekIndex: w,
        weekday: 1,
        assigneeId: c.p1._id.toHexString(),
      })),
      ...[0, 1, 2, 3].flatMap((w) => [
        { taskId: twice, weekIndex: w, weekday: 3 },
        { taskId: twice, weekIndex: w, weekday: 6 },
      ]),
    ]);
    await c.nightly();
    const ctx = c.t.systemCtx();
    const byDay = async (key: string, taskId: string) =>
      (await findOccurrences(c.t.db, { taskId: new ObjectId(taskId) })).find(
        (o) => toDayKey(o.date) === key,
      )!;

    const past = await byDay('2026-09-14', weekly); // stays open, before "today"
    const done = await byDay('2026-09-21', weekly);
    await updateOccurrence(
      ctx,
      done._id,
      { status: 'done', completedAt: new Date(), completedBy: c.p1._id },
      { action: 'complete' },
    );
    const skipped = await byDay('2026-09-23', twice);
    await updateOccurrence(ctx, skipped._id, { status: 'skipped' }, { action: 'skip' });
    const dragged = await byDay('2026-09-28', weekly);
    await updateOccurrence(
      ctx,
      dragged._id,
      { date: new Date('2026-09-28T22:00:00Z') },
      { action: 'reschedule' },
    );
    const [adhoc] = await insertOccurrencesIdempotent(
      ctx,
      [
        {
          ...done,
          _id: new ObjectId(),
          date: new Date('2026-09-29T22:00:00Z'),
          plannedDate: new Date('2026-09-29T22:00:00Z'),
          status: 'open',
          completedAt: null,
          completedBy: null,
          origin: 'adhoc',
          planId: null,
        },
      ],
      {},
    );

    // Two days later a new plan is activated
    c.t.clock.set('2026-09-16T08:00:00.000Z');
    const created = await c.t.app.inject({
      method: 'POST',
      url: '/api/cycle-plans',
      headers: asProfile(c.p1),
      payload: { name: 'Nieuw' },
    });
    const newPlanId = created.json<{ _id: string }>()._id;
    await c.putSlots(
      newPlanId,
      [0, 1, 2, 3].map((w) => ({
        taskId: twice,
        weekIndex: w,
        weekday: 4,
        assigneeId: c.p2._id.toHexString(),
      })),
    );

    const oldOpenFuture = await findOccurrences(c.t.db, {
      planId: new ObjectId(c.planId),
      status: 'open',
      origin: 'generated',
      date: { $gte: new Date('2026-09-15T22:00:00Z') },
      $expr: { $eq: ['$date', '$plannedDate'] },
    });
    expect(oldOpenFuture.length).toBeGreaterThan(0);

    const { result, entries } = await expectAudited(
      c.t,
      () =>
        c.t.app.inject({
          method: 'POST',
          url: `/api/cycle-plans/${newPlanId}/activate`,
          headers: asProfile(c.p1),
        }),
      { entity: 'cyclePlan', action: 'activate', source: 'ui', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(200);
    expect(entries[0]!.actorId).toEqual(c.p1._id);
    const body = result.json<{ removed: number; runId: string; plan: { active: boolean } }>();
    expect(body.plan.active).toBe(true);
    expect(body.removed).toBe(oldOpenFuture.length);

    // kept
    for (const kept of [past, done, skipped, dragged, adhoc!]) {
      expect(
        await c.t.db.collection(COLLECTIONS.occurrences).countDocuments({ _id: kept._id }),
        toDayKey(kept.date),
      ).toBe(1);
    }
    // replaced
    expect(await countOccurrences(c.t.db, { _id: { $in: oldOpenFuture.map((o) => o._id) } })).toBe(
      0,
    );
    const deletions = await c.t.db
      .collection(COLLECTIONS.auditLog)
      .find({ entity: 'occurrence', action: 'delete' })
      .toArray();
    expect(deletions).toHaveLength(oldOpenFuture.length);
    expect(deletions.every((d) => d.source === 'system' && d.meta?.runId === body.runId)).toBe(
      true,
    );

    // regenerated from the new plan: Thursdays from today in cycle 0 and all of cycle 1
    const fresh = await findOccurrences(c.t.db, { planId: new ObjectId(newPlanId) });
    expect(dayKeys(fresh)).toEqual([
      '2026-09-17',
      '2026-09-24',
      '2026-10-01',
      '2026-10-08',
      '2026-10-15',
      '2026-10-22',
      '2026-10-29',
      '2026-11-05',
    ]);
    expect(fresh.every((o) => c.p2._id.equals(o.assigneeId!))).toBe(true);

    // only one active plan; the old one's deactivation is audited
    const plans = await c.t.db.collection(COLLECTIONS.cyclePlans).find({ active: true }).toArray();
    expect(plans.map((p) => p._id.toHexString())).toEqual([newPlanId]);
    const deactivated = await c.t.db
      .collection(COLLECTIONS.auditLog)
      .findOne({
        entity: 'cyclePlan',
        entityId: new ObjectId(c.planId),
        action: 'update',
        'after.active': false,
      });
    expect(deactivated).not.toBeNull();
  });

  it('returns 404 for an unknown plan and requires a profile', async () => {
    const c = await setup();
    const unknown = await c.t.app.inject({
      method: 'POST',
      url: '/api/cycle-plans/0123456789abcdef01234567/activate',
      headers: asProfile(c.p1),
    });
    expect(unknown.statusCode).toBe(404);
    const anonymous = await c.t.app.inject({
      method: 'POST',
      url: `/api/cycle-plans/${c.planId}/activate`,
    });
    expect(anonymous.statusCode).toBe(400);
  });
});

describe('scheduler', () => {
  it('is disabled with DISABLE_SCHEDULER=true', async () => {
    t = await createTestApp();
    expect(startScheduler(t.app)).toBeNull();
  });

  it('starts and stops when enabled', async () => {
    t = await createTestApp({ env: { DISABLE_SCHEDULER: 'false' } });
    const handle = startScheduler(t.app);
    expect(handle).not.toBeNull();
    handle!.stop();
  });

  it('schedules audit retention and the morning notification when configured', async () => {
    t = await createTestApp({
      env: {
        DISABLE_SCHEDULER: 'false',
        NOTIFY_TYPE: 'ntfy',
        NOTIFY_URL: 'https://ntfy.example/huis',
        AUDIT_RETENTION_DAYS: '365',
      },
    });
    const handle = startScheduler(t.app);
    const scheduled = new Map(
      [...cron.getTasks().values()].map((task) => [task.name, task.getPattern()]),
    );
    expect(Object.fromEntries(scheduled)).toMatchObject({
      'nightly-generation': '0 3 * * *',
      'audit-retention': '45 3 * * *',
      'morning-notify': '30 7 * * *',
    });
    handle!.stop();
  });
});
