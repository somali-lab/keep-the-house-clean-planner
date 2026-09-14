import { addDays, fromDayKey } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { PDFParse } from 'pdf-parse';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { findOccurrences } from '../src/data/occurrences.ts';
import type { UserDoc } from '../src/data/users.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

// Anchor Monday 2026-09-14. Cycle starts: 14 Sep, 12 Oct, 9 Nov, 7 Dec.
const CYCLE_STARTS = ['2026-09-14', '2026-10-12', '2026-11-09', '2026-12-07'];

interface DueJson {
  taskId: string;
  taskName: string;
  roomName: string | null;
  intervalKey: string;
  intervalLabel: string;
  periodDays: number;
  daysSince: number;
  ratio: number;
  state: 'ok' | 'due' | 'overdue';
  lastCompletedAt: string | null;
  nextOccurrence: { id: string; date: string; assigneeId: string | null } | null;
}

let t: TestApp;
let p1: UserDoc;
let badkamer: string;
let stofzuigen: string;
let ramen: string;
let tidyWarnings: { code: string; taskId?: string }[];
let lastNightly: { due: { due: number; overdue: number } };

const headers = () => asProfile(p1);

async function nightly() {
  const res = await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: headers() });
  expect(res.statusCode, res.body).toBe(200);
  return res.json<{ due: { due: number; overdue: number } }>();
}

async function openInCycle(taskId: string, start: string) {
  return findOccurrences(t.db, {
    taskId: new ObjectId(taskId),
    status: 'open',
    date: { $gte: fromDayKey(start), $lt: fromDayKey(addDays(start, 28)) },
  });
}

beforeAll(async () => {
  t = await createTestApp({ now: `${CYCLE_STARTS[0]}T06:00:00.000Z` });
  [p1] = await seededUsers(t);
  const room = await seededRoom(t, 'Badkamer');
  const create = async (name: string, intervalKey: string) =>
    (
      await t.app.inject({
        method: 'POST',
        url: '/api/tasks',
        headers: headers(),
        payload: { name, roomId: room._id.toHexString(), intervalKey, durationMinutes: 20 },
      })
    ).json<{ _id: string }>()._id;
  badkamer = await create('Badkamer schoonmaken', '1w');
  stofzuigen = await create('Stofzuigen', '1w');
  ramen = await create('Ramen lappen', 'quarter');

  const planId = (await findActivePlan(t.db))!._id.toHexString();
  const P1 = p1._id.toHexString();
  const put = await t.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${planId}/slots`,
    headers: headers(),
    payload: {
      slots: [0, 1, 2, 3].flatMap((w) => [
        { taskId: badkamer, weekIndex: w, weekday: 1, assigneeId: P1 },
        { taskId: stofzuigen, weekIndex: w, weekday: 2, assigneeId: null },
      ]),
    },
  });
  expect(put.statusCode, put.body).toBe(200);
  tidyWarnings = put.json<{ warnings: { code: string; taskId?: string }[] }>().warnings;

  // Three cycles in which every bathroom clean is skipped.
  for (const start of CYCLE_STARTS.slice(0, 3)) {
    t.clock.set(`${start}T06:00:00.000Z`);
    await nightly();
    for (const occ of await openInCycle(badkamer, start)) {
      const res = await t.app.inject({
        method: 'PATCH',
        url: `/api/occurrences/${occ._id.toHexString()}`,
        headers: headers(),
        payload: { action: 'skip', reason: 'geen zin' },
      });
      expect(res.statusCode, res.body).toBe(200);
    }
  }

  // Vacuuming was done on the last Sunday of cycle 2.
  t.clock.set('2026-12-06T10:00:00.000Z');
  const [lastVacuum] = (await openInCycle(stofzuigen, CYCLE_STARTS[2]!)).slice(-1);
  const done = await t.app.inject({
    method: 'PATCH',
    url: `/api/occurrences/${lastVacuum!._id.toHexString()}`,
    headers: headers(),
    payload: { action: 'complete' },
  });
  expect(done.statusCode, done.body).toBe(200);

  t.clock.set(`${CYCLE_STARTS[3]}T06:00:00.000Z`);
  lastNightly = await nightly();
}, 120_000);

afterAll(async () => {
  await t.close();
});

async function dueList(): Promise<DueJson[]> {
  const res = await t.app.inject({ method: 'GET', url: '/api/due' });
  expect(res.statusCode, res.body).toBe(200);
  return res.json<DueJson[]>();
}

describe('GET /api/due', () => {
  it('shows a task skipped for three cycles as overdue while the grid looks tidy', async () => {
    expect(tidyWarnings.filter((w) => w.taskId === badkamer)).toEqual([]);

    const [first] = await dueList();
    expect(first).toMatchObject({
      taskId: badkamer,
      taskName: 'Badkamer schoonmaken',
      roomName: 'Badkamer',
      intervalLabel: '1x per week',
      periodDays: 7,
      daysSince: 84,
      ratio: 12,
      state: 'overdue',
      lastCompletedAt: null,
    });
    // the grid still has it planned today
    expect(first!.nextOccurrence).toMatchObject({ date: '2026-12-07', assigneeId: p1._id.toHexString() });
  });

  it('ranks all active tasks and keeps recently done tasks ok', async () => {
    const list = await dueList();
    expect(list.map((i) => i.taskId)).toEqual([badkamer, ramen, stofzuigen]);
    expect(list.find((i) => i.taskId === stofzuigen)).toMatchObject({ daysSince: 1, state: 'ok' });
    expect(list.find((i) => i.taskId === ramen)).toMatchObject({
      daysSince: 84,
      periodDays: 91,
      state: 'ok',
      nextOccurrence: null,
    });
  });

  it('marks a quarterly task due exactly at its period, based on creation date', async () => {
    t.clock.set('2026-12-14T06:00:00.000Z'); // 91 days after 14 Sep
    const ramenItem = (await dueList()).find((i) => i.taskId === ramen);
    expect(ramenItem).toMatchObject({ daysSince: 91, ratio: 1, state: 'due' });
    t.clock.set(`${CYCLE_STARTS[3]}T06:00:00.000Z`);
  });

  it('drops deactivated tasks from the list', async () => {
    const res = await t.app.inject({ method: 'PATCH', url: `/api/tasks/${ramen}`, headers: headers(), payload: { active: false } });
    expect(res.statusCode).toBe(200);
    expect((await dueList()).map((i) => i.taskId)).not.toContain(ramen);
    await t.app.inject({ method: 'PATCH', url: `/api/tasks/${ramen}`, headers: headers(), payload: { active: true } });
  });

  it('is summarised by the nightly job', () => {
    expect(lastNightly.due).toEqual({ due: 0, overdue: 1 });
  });
});

describe('GET /api/export/pdf/due', { timeout: 60_000 }, () => {
  it('prints the overdue task with its state spelled out', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/api/export/pdf/due' });
    expect(res.statusCode, res.body).toBe(200);
    expect(res.headers['content-disposition']).toBe('attachment; filename="achterstand-2026-12-07.pdf"');
    const parser = new PDFParse({ data: res.rawPayload });
    try {
      // Names may wrap inside a narrow table cell; compare with collapsed whitespace.
      const text = (await parser.getText()).text.replace(/\s+/g, ' ');
      expect(text).toContain('Badkamer schoonmaken');
      expect(text).toContain('Flink achter');
      expect(text).not.toContain('Stofzuigen');
    } finally {
      await parser.destroy();
    }
  });
});
