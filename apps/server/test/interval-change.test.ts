import { addDays, cycleStart, fromDayKey } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { findOccurrences, type OccurrenceDoc } from '../src/data/occurrences.ts';
import type { UserDoc } from '../src/data/users.ts';
import { captureWrites } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

/**
 * Plan §1.3.2: a change to a task's interval or duration takes effect from the
 * next generation. Occurrences that already exist keep their snapshots.
 */

const ANCHOR = '2026-09-14';

let t: TestApp;
let p1: UserDoc;
let taskId: string;
let planId: string;

const inCycle = (index: number) => ({
  taskId: new ObjectId(taskId),
  date: { $gte: fromDayKey(cycleStart(index, ANCHOR)), $lt: fromDayKey(addDays(cycleStart(index, ANCHOR), 28)) },
});

/** Everything except the fields a regeneration would never touch, for exact comparison. */
const snapshot = (docs: OccurrenceDoc[]) =>
  docs.map((d) => ({
    id: d._id.toHexString(),
    date: d.date.toISOString(),
    plannedDate: d.plannedDate.toISOString(),
    duration: d.durationMinutesSnapshot,
    name: d.taskNameSnapshot,
    updatedAt: d.updatedAt.toISOString(),
  }));

beforeAll(async () => {
  t = await createTestApp({ now: `${ANCHOR}T06:00:00.000Z` });
  [p1] = await seededUsers(t);
  const room = await seededRoom(t, 'Badkamer');
  taskId = (
    await t.app.inject({
      method: 'POST',
      url: '/api/tasks',
      headers: asProfile(p1),
      payload: { name: 'Badkamer schoonmaken', roomId: room._id.toHexString(), intervalKey: '1w', durationMinutes: 30 },
    })
  ).json<{ _id: string }>()._id;
  planId = (await findActivePlan(t.db))!._id.toHexString();
  const put = await t.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${planId}/slots`,
    headers: asProfile(p1),
    payload: { slots: [0, 1, 2, 3].map((w) => ({ taskId, weekIndex: w, weekday: 1, assigneeId: p1._id.toHexString() })) },
  });
  expect(put.statusCode, put.body).toBe(200);
  expect((await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(p1) })).statusCode).toBe(200);
});

afterAll(async () => {
  await t.close();
});

describe('changing interval and duration', () => {
  it('writes only the task, never the existing occurrences', async () => {
    const before = snapshot(await findOccurrences(t.db, { taskId: new ObjectId(taskId) }));
    expect(before).toHaveLength(8);
    expect(before.every((o) => o.duration === 30 && o.name === 'Badkamer schoonmaken')).toBe(true);

    t.clock.set('2026-09-16T08:00:00.000Z');
    const capture = await captureWrites(t, () =>
      t.app.inject({
        method: 'PATCH',
        url: `/api/tasks/${taskId}`,
        headers: asProfile(p1),
        payload: { durationMinutes: 45, intervalKey: '2wk', name: 'Badkamer grondig' },
      }),
    );
    expect(capture.result.statusCode, capture.result.body).toBe(200);
    expect([...new Set(capture.writes.map((w) => w.collection))]).toEqual(['tasks']);

    expect(snapshot(await findOccurrences(t.db, { taskId: new ObjectId(taskId) }))).toEqual(before);
  });

  it('does not touch existing occurrences when generation runs again for the same cycles', async () => {
    const before = snapshot(await findOccurrences(t.db, { taskId: new ObjectId(taskId) }));
    const res = await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(p1) });
    expect(res.json<{ generated: { inserted: number }[] }>().generated.map((g) => g.inserted)).toEqual([0, 0]);
    expect(snapshot(await findOccurrences(t.db, { taskId: new ObjectId(taskId) }))).toEqual(before);
  });

  it('keeps the template slots as they are (the interval change only affects validation)', async () => {
    const plan = await t.app.inject({ method: 'GET', url: `/api/cycle-plans/${planId}` });
    expect(plan.json<{ slots: unknown[] }>().slots).toHaveLength(4);
  });

  it('snapshots the new duration and name in the next generation', async () => {
    t.clock.set('2026-11-09T06:00:00.000Z'); // start of cycle 2
    const res = await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(p1) });
    expect(res.statusCode, res.body).toBe(200);

    const cycle2 = await findOccurrences(t.db, inCycle(2));
    expect(cycle2).toHaveLength(4);
    expect(cycle2.every((o) => o.durationMinutesSnapshot === 45 && o.taskNameSnapshot === 'Badkamer grondig')).toBe(true);

    const older = [...(await findOccurrences(t.db, inCycle(0))), ...(await findOccurrences(t.db, inCycle(1)))];
    expect(older).toHaveLength(8);
    expect(older.every((o) => o.durationMinutesSnapshot === 30 && o.taskNameSnapshot === 'Badkamer schoonmaken')).toBe(true);
  });
});
