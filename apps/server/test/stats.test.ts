import type { CompletionResponse, DeviationsResponse, IntervalsResponse, OccurrenceView, WorkloadResponse } from '@huishoudplanner/shared';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import type { UserDoc } from '../src/data/users.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

/**
 * Fixed dataset. Anchor Monday 2026-09-14; cycle 0 = 14 Sep–11 Oct, cycle 1 = 12 Oct–8 Nov.
 *   A "Badkamer schoonmaken" (Badkamer, 1w, 30 min): every Monday, Persoon 1
 *   B "Keuken dweilen"       (Keuken, 2wk, 20 min): Thursday of weeks 1 and 3, Persoon 2
 *   C "Ramen lappen"         (Woonkamer, 4wk, 45 min): Saturday of week 2, unassigned
 * Actions in cycle 0:
 *   A 14 Sep done by P1 · A 21 Sep done by P2 · A 28 Sep skipped · A 5 Oct left open
 *   B 17 Sep done by P2 · B 1 Oct done by P2 (on 2 Oct)
 *   C moved from 26 to 27 Sep, then done by P1 (claims it: assignee becomes P1)
 * Cycle 1: A 12 Oct done by P1; the rest still to come.
 * Statistics are asked on Wednesday 14 Oct 2026.
 */

let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
const task: Record<'A' | 'B' | 'C', string> = { A: '', B: '', C: '' };

async function occurrenceId(taskId: string, date: string): Promise<string> {
  const res = await t.app.inject({ method: 'GET', url: `/api/occurrences?from=${date}&to=${date}` });
  const occ = res.json<OccurrenceView[]>().find((o) => o.taskId === taskId);
  if (!occ) throw new Error(`no occurrence of ${taskId} on ${date}`);
  return occ._id;
}

async function act(taskId: string, date: string, at: string, payload: Record<string, unknown>) {
  t.clock.set(at);
  const res = await t.app.inject({
    method: 'PATCH',
    url: `/api/occurrences/${await occurrenceId(taskId, date)}`,
    headers: asProfile(p1),
    payload,
  });
  expect(res.statusCode, res.body).toBe(200);
}

const get = async <T>(url: string): Promise<T> => {
  const res = await t.app.inject({ method: 'GET', url });
  expect(res.statusCode, res.body).toBe(200);
  return res.json<T>();
};

beforeAll(async () => {
  t = await createTestApp({ now: '2026-09-14T06:00:00.000Z' });
  [p1, p2] = await seededUsers(t);
  const headers = asProfile(p1);
  const create = async (name: string, room: string, intervalKey: string, durationMinutes: number) =>
    (
      await t.app.inject({
        method: 'POST',
        url: '/api/tasks',
        headers,
        payload: { name, roomId: (await seededRoom(t, room))._id.toHexString(), intervalKey, durationMinutes },
      })
    ).json<{ _id: string }>()._id;
  task.A = await create('Badkamer schoonmaken', 'Badkamer', '1w', 30);
  task.B = await create('Keuken dweilen', 'Keuken', '2wk', 20);
  task.C = await create('Ramen lappen', 'Woonkamer', '4wk', 45);

  const P1 = p1._id.toHexString();
  const P2 = p2._id.toHexString();
  const planId = (await findActivePlan(t.db))!._id.toHexString();
  const put = await t.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${planId}/slots`,
    headers,
    payload: {
      slots: [
        ...[0, 1, 2, 3].map((w) => ({ taskId: task.A, weekIndex: w, weekday: 1, assigneeId: P1 })),
        { taskId: task.B, weekIndex: 0, weekday: 4, assigneeId: P2 },
        { taskId: task.B, weekIndex: 2, weekday: 4, assigneeId: P2 },
        { taskId: task.C, weekIndex: 1, weekday: 6, assigneeId: null },
      ],
    },
  });
  expect(put.statusCode, put.body).toBe(200);
  expect((await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers })).statusCode).toBe(200);

  await act(task.A, '2026-09-14', '2026-09-14T18:00:00.000Z', { action: 'complete' });
  await act(task.B, '2026-09-17', '2026-09-17T18:00:00.000Z', { action: 'complete', completedBy: P2 });
  await act(task.A, '2026-09-21', '2026-09-21T18:00:00.000Z', { action: 'complete', completedBy: P2 });
  await act(task.C, '2026-09-26', '2026-09-25T10:00:00.000Z', { action: 'reschedule', date: '2026-09-27' });
  await act(task.C, '2026-09-27', '2026-09-27T10:00:00.000Z', { action: 'complete' });
  await act(task.A, '2026-09-28', '2026-09-28T18:00:00.000Z', { action: 'skip', reason: 'ziek' });
  await act(task.B, '2026-10-01', '2026-10-02T09:00:00.000Z', { action: 'complete', completedBy: P2 });
  await act(task.A, '2026-10-12', '2026-10-12T18:00:00.000Z', { action: 'complete' });

  t.clock.set('2026-10-14T08:00:00.000Z');
}, 60_000);

afterAll(async () => {
  await t.close();
});

describe('GET /api/stats/workload', () => {
  it('reports planned (by assignee) and done (by completedBy) snapshot minutes per week and cycle', async () => {
    const P1 = p1._id.toHexString();
    const P2 = p2._id.toHexString();
    const { cycles } = await get<WorkloadResponse>('/api/stats/workload?cycles=2');
    expect(cycles.map((c) => [c.index, c.startDate, c.endDate])).toEqual([
      [0, '2026-09-14', '2026-10-11'],
      [1, '2026-10-12', '2026-11-08'],
    ]);

    const minutes = (users: { userId: string; plannedMinutes: number; doneMinutes: number }[], id: string) => {
      const u = users.find((x) => x.userId === id)!;
      return [u.plannedMinutes, u.doneMinutes];
    };

    const [c0, c1] = cycles;
    expect(c0!.weeks.map((w) => w.startDate)).toEqual(['2026-09-14', '2026-09-21', '2026-09-28', '2026-10-05']);
    expect(c0!.weeks.map((w) => minutes(w.users, P1))).toEqual([
      [30, 30],
      [75, 45],
      [30, 0],
      [30, 0],
    ]);
    expect(c0!.weeks.map((w) => minutes(w.users, P2))).toEqual([
      [20, 20],
      [0, 30],
      [20, 20],
      [0, 0],
    ]);
    expect(minutes(c0!.users, P1)).toEqual([165, 75]);
    expect(minutes(c0!.users, P2)).toEqual([40, 70]);
    expect(c0!.unassignedPlannedMinutes).toBe(0);

    expect(c1!.weeks.map((w) => minutes(w.users, P1))).toEqual([
      [30, 30],
      [30, 0],
      [30, 0],
      [30, 0],
    ]);
    expect(minutes(c1!.users, P1)).toEqual([120, 30]);
    expect(minutes(c1!.users, P2)).toEqual([40, 0]);
    expect(c1!.unassignedPlannedMinutes).toBe(45);
    expect(c1!.weeks.map((w) => w.unassignedPlannedMinutes)).toEqual([0, 45, 0, 0]);
  });

  it('limits to the requested number of recent cycles', async () => {
    const { cycles } = await get<WorkloadResponse>('/api/stats/workload?cycles=1');
    expect(cycles.map((c) => c.index)).toEqual([1]);
  });

  it('limits workload to this week or the last three calendar weeks', async () => {
    const current = await get<WorkloadResponse>('/api/stats/workload?weeks=1');
    expect(current.cycles).toHaveLength(1);
    expect(current.cycles[0]!.weeks.map((week) => week.startDate)).toEqual(['2026-10-12']);

    const recent = await get<WorkloadResponse>('/api/stats/workload?weeks=3');
    expect(recent.cycles.flatMap((cycle) => cycle.weeks.map((week) => week.startDate))).toEqual([
      '2026-09-28',
      '2026-10-05',
      '2026-10-12',
    ]);
  });

  it('does not change history when a task duration is halved', async () => {
    const before = await get<WorkloadResponse>('/api/stats/workload?cycles=2');
    const res = await t.app.inject({ method: 'PATCH', url: `/api/tasks/${task.A}`, headers: asProfile(p1), payload: { durationMinutes: 15 } });
    expect(res.statusCode).toBe(200);
    expect(await get<WorkloadResponse>('/api/stats/workload?cycles=2')).toEqual(before);
    await t.app.inject({ method: 'PATCH', url: `/api/tasks/${task.A}`, headers: asProfile(p1), payload: { durationMinutes: 30 } });
  });
});

describe('GET /api/stats/completion', () => {
  it('groups by task: done / (done + skipped + still open in the past), worst first', async () => {
    const { rows } = await get<CompletionResponse>('/api/stats/completion?cycles=2&groupBy=task');
    expect(rows).toEqual([
      { key: task.A, name: 'Badkamer schoonmaken', done: 3, skipped: 1, missed: 1, rate: 0.6 },
      { key: task.B, name: 'Keuken dweilen', done: 2, skipped: 0, missed: 0, rate: 1 },
      { key: task.C, name: 'Ramen lappen', done: 1, skipped: 0, missed: 0, rate: 1 },
    ]);
  });

  it('groups by room', async () => {
    const { rows } = await get<CompletionResponse>('/api/stats/completion?cycles=2&groupBy=room');
    expect(rows.map((r) => [r.name, r.done, r.skipped, r.missed, r.rate])).toEqual([
      ['Badkamer', 3, 1, 1, 0.6],
      ['Keuken', 2, 0, 0, 1],
      ['Woonkamer', 1, 0, 0, 1],
    ]);
  });

  it('groups by assignee', async () => {
    const { rows } = await get<CompletionResponse>('/api/stats/completion?cycles=2&groupBy=user');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toMatchObject({ key: p1._id.toHexString(), name: 'Persoon 1', done: 4, skipped: 1, missed: 1 });
    expect(rows[0]!.rate).toBeCloseTo(4 / 6, 10);
    expect(rows[1]).toEqual({ key: p2._id.toHexString(), name: 'Persoon 2', done: 2, skipped: 0, missed: 0, rate: 1 });
  });

  it('validates the query', async () => {
    expect((await t.app.inject({ method: 'GET', url: '/api/stats/completion?cycles=2' })).statusCode).toBe(400);
    expect((await t.app.inject({ method: 'GET', url: '/api/stats/completion?groupBy=planet' })).statusCode).toBe(400);
    expect((await t.app.inject({ method: 'GET', url: '/api/stats/workload?cycles=0' })).statusCode).toBe(400);
    expect((await t.app.inject({ method: 'GET', url: '/api/stats/workload?weeks=4' })).statusCode).toBe(400);
  });

  it('applies the week period to completion-by results', async () => {
    const { rows } = await get<CompletionResponse>('/api/stats/completion?weeks=1&groupBy=task');
    expect(rows).toEqual([
      { key: task.A, name: 'Badkamer schoonmaken', done: 1, skipped: 0, missed: 0, rate: 1 },
    ]);
  });
});

describe('GET /api/stats/intervals', () => {
  it('compares average days between completions with the configured period, most wishful first', async () => {
    const { rows } = await get<IntervalsResponse>('/api/stats/intervals?cycles=2');
    expect(rows.map((r) => [r.name, r.periodDays, r.completions, r.averageDays])).toEqual([
      ['Badkamer schoonmaken', 7, 3, 14],
      ['Keuken dweilen', 14, 2, 15],
      ['Ramen lappen', 28, 1, null],
    ]);
    expect(rows[0]!.deviation).toBe(2);
    expect(rows[1]!.deviation).toBeCloseTo(15 / 14, 10);
    expect(rows[2]!.deviation).toBeNull();
  });
});

describe('GET /api/stats/deviations', () => {
  it('separates plan changes from early or late completion', async () => {
    const { rows } = await get<DeviationsResponse>('/api/stats/deviations?cycles=2');
    expect(rows).toEqual([
      {
        taskId: task.B,
        name: 'Keuken dweilen',
        completions: 2,
        averagePlanningShiftDays: 0,
        averageCompletionDelayDays: 0.5,
        early: 0,
        onTime: 1,
        late: 1,
      },
      {
        taskId: task.C,
        name: 'Ramen lappen',
        completions: 1,
        averagePlanningShiftDays: 1,
        averageCompletionDelayDays: 0,
        early: 0,
        onTime: 1,
        late: 0,
      },
      {
        taskId: task.A,
        name: 'Badkamer schoonmaken',
        completions: 3,
        averagePlanningShiftDays: 0,
        averageCompletionDelayDays: 0,
        early: 0,
        onTime: 3,
        late: 0,
      },
    ]);
  });
});
