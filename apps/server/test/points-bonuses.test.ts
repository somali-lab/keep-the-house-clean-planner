/* eslint-disable no-restricted-syntax -- these tests write corrupt and drifted data straight to the database, which no repository does, to prove the reconciliation copes with it */
import type { LightMyRequestResponse } from 'fastify';
import {
  BONUS_KINDS,
  toDayKey,
  type OccurrenceView,
  type PointsBalancesResponse,
  type PointsEntriesResponse,
} from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { applyBonusEntryChanges, findBonusPointEntries, type PointEntryDoc } from '../src/data/points.ts';
import { reconcilePoints, reconcilePointsSafely } from '../src/domain/points.ts';
import type { ExportFile } from '../src/domain/transfer.ts';
import { runNightly } from '../src/jobs/nightly.ts';
import { captureWrites } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';
import type { UserDoc } from '../src/data/users.ts';

/**
 * ADR-0012: week and cycle bonuses are derived ledger entries that only the reconciliation writes.
 * Monday 14 Sep 2026 is the first day of cycle 0 (14 Sep to 11 Oct). Every cycle has one task of 30
 * minutes (3 points) on Monday (person 1), Tuesday (person 2) and Wednesday (nobody) of its first week.
 */
const apps: TestApp[] = [];

afterEach(async () => {
  for (const app of apps.splice(0)) await app.close();
});

const AMOUNTS = { weekDone: 5, weekOnTime: 3, cycleDone: 20, cycleOnTime: 10 };

interface Fixture {
  t: TestApp;
  p1: UserDoc;
  p2: UserDoc;
  /** The id of the occurrence on a day (the first cycle and the second cycle are both generated). */
  occurrence: (date: string) => Promise<string>;
  call: (method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', url: string, payload?: Record<string, unknown>, actor?: UserDoc) => Promise<LightMyRequestResponse>;
  /** Moves the clock and runs the reconciliation the nightly job runs. */
  reconcileAt: (now: string) => ReturnType<typeof reconcilePoints>;
  /** Moves the clock and performs one request as the person. */
  at: (now: string, method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', url: string, payload?: Record<string, unknown>, actor?: UserDoc) => Promise<LightMyRequestResponse>;
}

async function fixture(options: { amounts?: boolean } = {}): Promise<Fixture> {
  const t = await createTestApp({ now: '2026-09-14T06:00:00.000Z' });
  apps.push(t);
  const [p1, p2] = await seededUsers(t);
  const call: Fixture['call'] = (method, url, payload, actor = p1) =>
    t.app.inject({ method, url, headers: asProfile(actor), ...(payload ? { payload } : {}) });
  const roomId = (await seededRoom(t, 'Woonkamer'))._id.toHexString();
  const created = await call('POST', '/api/tasks', { name: 'Stofzuigen', roomId, intervalKey: '1w', durationMinutes: 30 });
  expect(created.statusCode, created.body).toBe(201);
  const task = created.json<{ _id: string }>()._id;
  const plan = (await findActivePlan(t.db))!;
  const slots = [
    { taskId: task, weekIndex: 0, weekday: 1, assigneeId: p1._id.toHexString() },
    { taskId: task, weekIndex: 0, weekday: 2, assigneeId: p2._id.toHexString() },
    { taskId: task, weekIndex: 0, weekday: 3, assigneeId: null },
  ];
  expect((await call('PUT', `/api/cycle-plans/${plan._id.toHexString()}/slots`, { slots })).statusCode).toBe(200);
  expect((await call('POST', '/api/jobs/nightly')).statusCode).toBe(200);
  if (options.amounts !== false) {
    const res = await call('PATCH', '/api/settings', { periodBonuses: AMOUNTS });
    expect(res.statusCode, res.body).toBe(200);
  }
  const occurrence = async (date: string) => {
    const res = await t.app.inject({ method: 'GET', url: `/api/occurrences?from=${date}&to=${date}` });
    return res.json<OccurrenceView[]>().find((o) => o.taskId === task)!._id;
  };
  const at: Fixture['at'] = (now, method, url, payload, actor) => {
    t.clock.set(now);
    return call(method, url, payload, actor);
  };
  const reconcileAt = (now: string) => {
    t.clock.set(now);
    return reconcilePoints(t.systemCtx(), 'nightly');
  };
  return { t, p1, p2, occurrence, call, reconcileAt, at };
}

const complete = { action: 'complete' } as const;

/** Person 1 and person 2 do their task on time in the week of 14 September; the unassigned one stays open. */
async function doTheWeek(f: Fixture): Promise<void> {
  const monday = await f.occurrence('2026-09-14');
  const tuesday = await f.occurrence('2026-09-15');
  expect((await f.at('2026-09-14T07:00:00.000Z', 'PATCH', `/api/occurrences/${monday}`, complete, f.p1)).statusCode).toBe(200);
  expect((await f.at('2026-09-15T07:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, complete, f.p2)).statusCode).toBe(200);
}

const auditOf = (t: TestApp) =>
  t.db.collection(COLLECTIONS.auditLog).find({ entity: 'points', action: 'recompute' }).sort({ _id: 1 }).toArray();

/** A readable view of the stored bonuses: kind, person, first day of the period and amount, in a stable order. */
async function bonuses(f: Pick<Fixture, 't' | 'p1' | 'p2'>): Promise<string[]> {
  const name = (id: ObjectId) => (id.equals(f.p1._id) ? 'p1' : id.equals(f.p2._id) ? 'p2' : id.toHexString());
  return (await findBonusPointEntries(f.t.db))
    .map((entry) => `${entry.kind} ${name(entry.personId)} ${toDayKey(entry.periodStart!, 'Europe/Amsterdam')} ${entry.amount}`)
    .sort();
}

const WEEK_BONUSES = [
  'bonus_week_done p1 2026-09-14 5',
  'bonus_week_done p2 2026-09-14 5',
  'bonus_week_ontime p1 2026-09-14 3',
  'bonus_week_ontime p2 2026-09-14 3',
];

describe('finalisation by the nightly job', () => {
  it('writes nothing on Sunday 23:00 local, creates the bonuses on Monday 03:00 with one audit entry, and never again', async () => {
    const f = await fixture();
    await doTheWeek(f);
    const { t } = f;

    // Sunday 20 September, 23:00 local time (21:00Z): the week has not ended, so the ledger is not touched.
    // (Generation re-offers the slots it already has, which is not a change and is audited as none.)
    t.clock.set('2026-09-20T21:00:00.000Z');
    await runNightly(t.systemCtx());
    const sunday = await captureWrites(t, () => runNightly(t.systemCtx()));
    expect(sunday.writes.filter((w) => w.collection === COLLECTIONS.pointEntries)).toEqual([]);
    expect(sunday.auditInserts).toBe(0);
    expect(await bonuses(f)).toEqual([]);
    expect(await auditOf(t)).toHaveLength(0);

    // Monday 21 September, 03:00 local time (01:00Z): the week that ended at midnight is finalised.
    t.clock.set('2026-09-21T01:00:00.000Z');
    const monday = await captureWrites(t, () => runNightly(t.systemCtx()));
    expect(monday.writes.filter((w) => w.collection === COLLECTIONS.pointEntries)).not.toEqual([]);
    expect(await bonuses(f)).toEqual(WEEK_BONUSES);

    const audit = await auditOf(t);
    expect(audit).toHaveLength(1);
    expect(audit[0]).toMatchObject({
      entity: 'points',
      action: 'recompute',
      source: 'system',
      meta: { trigger: 'nightly', created: 0, updated: 0, removed: 0, bonusesCreated: 4, bonusesRemoved: 0, bonusChangesTotal: 4, bonusChangesTruncated: false },
    });
    expect((audit[0]!.meta as { bonusChanges: { key: string; change: string; amount: number }[] }).bonusChanges).toEqual(
      expect.arrayContaining([
        { key: `bonus_week_done:${f.p1._id.toHexString()}:2026-09-14`, personId: f.p1._id.toHexString(), amount: 5, change: 'created' },
        { key: `bonus_week_ontime:${f.p2._id.toHexString()}:2026-09-14`, personId: f.p2._id.toHexString(), amount: 3, change: 'created' },
      ]),
    );

    const entry = (await findBonusPointEntries(t.db)).find((e) => e.kind === 'bonus_week_done' && e.personId.equals(f.p1._id)) as PointEntryDoc;
    expect(entry).toMatchObject({
      key: `bonus_week_done:${f.p1._id.toHexString()}:2026-09-14`,
      occurrenceId: null,
      taskId: null,
      titleSnapshot: '',
      source: 'recompute',
    });
    // Dated on the last day of the period, so a balance range that includes that day includes the bonus.
    expect(toDayKey(entry.date, 'Europe/Amsterdam')).toBe('2026-09-20');
    expect(toDayKey(entry.weekStart, 'Europe/Amsterdam')).toBe('2026-09-14');

    // A second run, and a restart, change nothing: a bonus is never awarded twice.
    const again = await captureWrites(t, () => runNightly(t.systemCtx()));
    expect(again.writes.filter((w) => w.collection === COLLECTIONS.pointEntries)).toEqual([]);
    expect(again.auditInserts).toBe(0);
    const restart = await captureWrites(t, () => reconcilePointsSafely(t.systemCtx(), 'startup'));
    expect(restart.writes).toEqual([]);
    expect(restart.auditInserts).toBe(0);
    expect(await auditOf(t)).toHaveLength(1);
  });

  it('pays nobody for skipped work, and does not let unassigned open work block anybody', async () => {
    const f = await fixture();
    const monday = await f.occurrence('2026-09-14');
    const tuesday = await f.occurrence('2026-09-15');
    expect((await f.at('2026-09-14T07:00:00.000Z', 'PATCH', `/api/occurrences/${monday}`, complete, f.p1)).statusCode).toBe(200);
    expect((await f.at('2026-09-15T07:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, { action: 'skip' }, f.p2)).statusCode).toBe(200);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    expect(await bonuses(f)).toEqual(['bonus_week_done p1 2026-09-14 5', 'bonus_week_ontime p1 2026-09-14 3']);
  });

  it('does nothing while the amounts are 0, which is the default', async () => {
    const f = await fixture({ amounts: false });
    await doTheWeek(f);
    const result = await f.reconcileAt('2026-09-21T01:00:00.000Z');
    expect(result).toMatchObject({ bonusesCreated: 0, bonusesRemoved: 0, bonusChangesTotal: 0 });
    expect(await bonuses(f)).toEqual([]);
    expect(await auditOf(f.t)).toHaveLength(0);
  });
});

describe('corrections after finalisation', () => {
  async function finalised(): Promise<Fixture> {
    const f = await fixture();
    await doTheWeek(f);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    expect(await bonuses(f)).toEqual(WEEK_BONUSES);
    return f;
  }

  it('removes the bonuses on an uncomplete, and a late check-off adds only "done"', async () => {
    const f = await finalised();
    const monday = await f.occurrence('2026-09-14');
    expect((await f.at('2026-09-22T08:00:00.000Z', 'PATCH', `/api/occurrences/${monday}`, { action: 'uncomplete' })).statusCode).toBe(200);
    // Check-offs and corrections never write a bonus: the ledger follows at the next run.
    expect(await bonuses(f)).toEqual(WEEK_BONUSES);

    const removed = await f.reconcileAt('2026-09-22T09:00:00.000Z');
    expect(removed).toMatchObject({ bonusesCreated: 0, bonusesRemoved: 2, bonusChangesTotal: 2 });
    expect(removed.bonusChanges.map((c) => [c.change, c.personId, c.amount])).toEqual([
      ['removed', f.p1._id.toHexString(), 5],
      ['removed', f.p1._id.toHexString(), 3],
    ]);
    expect(await bonuses(f)).toEqual(WEEK_BONUSES.filter((entry) => !entry.includes(' p1 ')));

    // Completed again, after the end of the week: done, but not on time.
    expect((await f.at('2026-09-23T08:00:00.000Z', 'PATCH', `/api/occurrences/${monday}`, complete)).statusCode).toBe(200);
    const late = await f.reconcileAt('2026-09-23T09:00:00.000Z');
    expect(late).toMatchObject({ bonusesCreated: 1, bonusesRemoved: 0 });
    expect(await bonuses(f)).toEqual(['bonus_week_done p1 2026-09-14 5', ...WEEK_BONUSES.filter((entry) => entry.includes(' p2 '))].sort());
    expect(await auditOf(f.t)).toHaveLength(3);
  });

  it('removes the on-time bonus when an administrator moves the completion past the cut-off', async () => {
    const f = await finalised();
    const tuesday = await f.occurrence('2026-09-15');
    const res = await f.at('2026-09-22T08:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, {
      action: 'edit_completion',
      date: '2026-09-15',
      completedAt: '2026-09-21T10:00:00.000Z',
      completedBy: f.p2._id.toHexString(),
    });
    expect(res.statusCode, res.body).toBe(200);
    await f.reconcileAt('2026-09-22T09:00:00.000Z');
    expect(await bonuses(f)).toEqual(WEEK_BONUSES.filter((entry) => entry !== 'bonus_week_ontime p2 2026-09-14 3'));
  });

  it('removes the bonuses when an administrator deletes the completion', async () => {
    const f = await finalised();
    const monday = await f.occurrence('2026-09-14');
    expect((await f.at('2026-09-22T08:00:00.000Z', 'DELETE', `/api/occurrences/${monday}`)).statusCode).toBe(200);
    // The set of person 1 is now empty, so nothing is earned.
    const result = await f.reconcileAt('2026-09-22T09:00:00.000Z');
    expect(result).toMatchObject({ bonusesRemoved: 2 });
    expect(await bonuses(f)).toEqual(WEEK_BONUSES.filter((entry) => !entry.includes(' p1 ')));
  });

  /** The occurrence document, to see what was frozen on it. */
  const stored = (f: Fixture, id: string) => f.t.db.collection(COLLECTIONS.occurrences).findOne({ _id: new ObjectId(id) });
  const occurrenceAudit = (f: Fixture, id: string) =>
    f.t.db.collection(COLLECTIONS.auditLog).find({ entity: 'occurrence', entityId: new ObjectId(id) }).sort({ _id: 1 }).toArray();

  it("keeps person 1's finalised bonus when she takes over person 2's overdue item, and person 2 gains nothing from it", async () => {
    const f = await finalised();
    const tuesday = await f.occurrence('2026-09-15');
    // Person 2 uncompletes Tuesday after the week ended: the item is open and overdue, and blocks person 2.
    expect((await f.at('2026-09-22T08:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, { action: 'uncomplete' }, f.p2)).statusCode).toBe(200);
    await f.reconcileAt('2026-09-22T09:00:00.000Z');
    expect(await bonuses(f)).toEqual(WEEK_BONUSES.filter((entry) => entry.includes(' p1 ')));

    // Person 1 takes it over and finishes it: the week of person 1 is not penalised, person 2 still has not done it.
    const before = (await occurrenceAudit(f, tuesday)).length;
    expect((await f.at('2026-09-23T08:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, { action: 'complete', takeOver: true }, f.p1)).statusCode).toBe(200);
    const doc = (await stored(f, tuesday))!;
    expect((doc.periodOwnerId as ObjectId).equals(f.p2._id)).toBe(true);
    expect((doc.assigneeId as ObjectId).equals(f.p1._id)).toBe(true);
    // The freeze is part of the same audited update: one more entry, no separate one.
    const audit = await occurrenceAudit(f, tuesday);
    expect(audit).toHaveLength(before + 1);
    expect(audit.at(-1)!.after).toMatchObject({ periodOwnerId: f.p2._id });
    const result = await f.reconcileAt('2026-09-23T09:00:00.000Z');
    expect(result).toMatchObject({ bonusesCreated: 0, bonusesRemoved: 0 });
    expect(await bonuses(f)).toEqual(WEEK_BONUSES.filter((entry) => entry.includes(' p1 ')));
  });

  it('counts a completion on behalf of the owner for the owner: done, but late', async () => {
    const f = await finalised();
    const tuesday = await f.occurrence('2026-09-15');
    expect((await f.at('2026-09-22T08:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, { action: 'uncomplete' }, f.p2)).statusCode).toBe(200);
    // Person 1 checks it off for person 2.
    expect(
      (await f.at('2026-09-23T08:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, { action: 'complete', completedBy: f.p2._id.toHexString() }, f.p1)).statusCode,
    ).toBe(200);
    await f.reconcileAt('2026-09-23T09:00:00.000Z');
    expect(await bonuses(f)).toEqual(
      [...WEEK_BONUSES.filter((entry) => entry.includes(' p1 ')), 'bonus_week_done p2 2026-09-14 5'].sort(),
    );
  });

  it('does not let claiming unassigned overdue work change anybody\'s bonus, and never blocks the claimer', async () => {
    const f = await finalised();
    const wednesday = await f.occurrence('2026-09-16');
    expect((await f.at('2026-09-22T08:00:00.000Z', 'POST', `/api/occurrences/${wednesday}/claim`, undefined, f.p2)).statusCode).toBe(200);
    expect((await stored(f, wednesday))!.periodOwnerId).toBeNull();
    await f.reconcileAt('2026-09-22T09:00:00.000Z');
    expect(await bonuses(f)).toEqual(WEEK_BONUSES);
    expect((await f.at('2026-09-23T08:00:00.000Z', 'PATCH', `/api/occurrences/${wednesday}`, complete, f.p2)).statusCode).toBe(200);
    await f.reconcileAt('2026-09-23T09:00:00.000Z');
    expect(await bonuses(f)).toEqual(WEEK_BONUSES);
  });

  it("does not move reassigned open overdue work into the new assignee's ended week", async () => {
    const f = await finalised();
    const tuesday = await f.occurrence('2026-09-15');
    expect((await f.at('2026-09-22T08:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, { action: 'uncomplete' }, f.p2)).statusCode).toBe(200);
    const assign = await f.at('2026-09-22T08:30:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, { action: 'assign', assigneeId: f.p1._id.toHexString() });
    expect(assign.statusCode, assign.body).toBe(200);
    expect(((await stored(f, tuesday))!.periodOwnerId as ObjectId).equals(f.p2._id)).toBe(true);
    await f.reconcileAt('2026-09-22T09:00:00.000Z');
    // Person 2 still owns the missed item of that week; person 1 keeps the bonuses, and has no extra item in the ended week.
    expect(await bonuses(f)).toEqual(WEEK_BONUSES.filter((entry) => entry.includes(' p1 ')));
    // Assigning the person who already has it freezes and writes nothing.
    const again = await captureWrites(f.t, () => f.call('PATCH', `/api/occurrences/${tuesday}`, { action: 'assign', assigneeId: f.p1._id.toHexString() }));
    expect(again.writes).toEqual([]);
  });

  it('freezes nothing while the planned week is still running', async () => {
    const f = await fixture();
    const tuesday = await f.occurrence('2026-09-15');
    expect((await f.at('2026-09-15T08:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, { action: 'assign', assigneeId: f.p1._id.toHexString() })).statusCode).toBe(200);
    expect((await stored(f, tuesday))!.periodOwnerId).toBeUndefined();
  });

  it('never lets recorded work block or be late, also when an administrator corrects its date to before its completion', async () => {
    const f = await finalised();
    const task = (await f.t.db.collection(COLLECTIONS.tasks).findOne({}))!._id.toHexString();
    // Person 1 records extra work on 23 September, then an administrator moves it into the ended week.
    const recorded = await f.at('2026-09-23T08:00:00.000Z', 'POST', '/api/occurrences', { taskId: task, date: '2026-09-23', done: true, assigneeId: f.p1._id.toHexString() });
    expect(recorded.statusCode, recorded.body).toBe(201);
    const id = recorded.json<{ _id: string }>()._id;
    const edit = await f.at('2026-09-23T09:00:00.000Z', 'PATCH', `/api/occurrences/${id}`, {
      action: 'edit_completion',
      date: '2026-09-16',
      completedAt: '2026-09-23T08:00:00.000Z',
      completedBy: f.p1._id.toHexString(),
    });
    expect(edit.statusCode, edit.body).toBe(200);
    const result = await f.reconcileAt('2026-09-23T10:00:00.000Z');
    expect(result).toMatchObject({ bonusesCreated: 0, bonusesRemoved: 0 });
    expect(await bonuses(f)).toEqual(WEEK_BONUSES);
  });

  it('leaves the bonuses of a person whose occurrence cannot be read, and counts it as skipped', async () => {
    const f = await finalised();
    const monday = await f.occurrence('2026-09-14');
    const tuesday = await f.occurrence('2026-09-15');
    const base = (await f.t.db.collection(COLLECTIONS.occurrences).findOne({ _id: new ObjectId(monday) }))!;
    await f.t.db.collection(COLLECTIONS.occurrences).insertOne({
      ...base,
      _id: new ObjectId(),
      origin: 'adhoc',
      planId: null,
      status: 'open',
      completedAt: null,
      completedBy: null,
      plannedDate: 'not-a-date',
      requestId: null,
    });
    // Person 1 would lose the bonuses (their Monday task is undone) and so would person 2; only person 2 can be evaluated.
    expect((await f.at('2026-09-22T08:00:00.000Z', 'PATCH', `/api/occurrences/${monday}`, { action: 'uncomplete' })).statusCode).toBe(200);
    expect((await f.at('2026-09-22T08:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, { action: 'uncomplete' }, f.p2)).statusCode).toBe(200);
    const result = await f.reconcileAt('2026-09-22T09:00:00.000Z');
    expect(result.skipped).toBe(1);
    expect(await bonuses(f)).toEqual(WEEK_BONUSES.filter((entry) => entry.includes(' p1 ')));
  });
});

describe('the bonus schedule', () => {
  it('leaves the periods that ended before the amounts were enabled or changed as they were', async () => {
    const f = await fixture({ amounts: false });
    await doTheWeek(f);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    expect(await bonuses(f)).toEqual([]);

    // Enabling bonuses never awards the past.
    expect((await f.at('2026-09-22T08:00:00.000Z', 'PATCH', '/api/settings', { periodBonuses: AMOUNTS })).statusCode).toBe(200);
    const enabled = await f.reconcileAt('2026-09-22T09:00:00.000Z');
    expect(enabled).toMatchObject({ bonusesCreated: 0, bonusesRemoved: 0 });
    expect(await bonuses(f)).toEqual([]);

    // The week of 12 October ends after the row, so it is paid with the amounts in force on its last day.
    const monday = await f.occurrence('2026-10-12');
    const tuesday = await f.occurrence('2026-10-13');
    expect((await f.at('2026-10-12T07:00:00.000Z', 'PATCH', `/api/occurrences/${monday}`, complete, f.p1)).statusCode).toBe(200);
    expect((await f.at('2026-10-13T07:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, complete, f.p2)).statusCode).toBe(200);
    await f.reconcileAt('2026-10-19T01:00:00.000Z');
    // Cycle 0 ended on 11 October, after the row, so its cycle bonuses include the week that was done before the amounts were set.
    const afterEnabling = await bonuses(f);
    expect(afterEnabling).toEqual(
      [
        'bonus_cycle_done p1 2026-09-14 20',
        'bonus_cycle_done p2 2026-09-14 20',
        'bonus_cycle_ontime p1 2026-09-14 10',
        'bonus_cycle_ontime p2 2026-09-14 10',
        'bonus_week_done p1 2026-10-12 5',
        'bonus_week_done p2 2026-10-12 5',
        'bonus_week_ontime p1 2026-10-12 3',
        'bonus_week_ontime p2 2026-10-12 3',
      ],
    );

    // Changing the amounts again never alters the periods that ended: only later ones use them.
    const doubled = { weekDone: 10, weekOnTime: 6, cycleDone: 40, cycleOnTime: 20 };
    expect((await f.at('2026-10-20T08:00:00.000Z', 'PATCH', '/api/settings', { periodBonuses: doubled })).statusCode).toBe(200);
    const changed = await f.reconcileAt('2026-10-20T09:00:00.000Z');
    expect(changed).toMatchObject({ bonusesCreated: 0, bonusesRemoved: 0 });
    expect(await bonuses(f)).toEqual(afterEnabling);
    // A full rebuild or a run on another day gives the same ledger.
    expect(await f.t.db.collection(COLLECTIONS.pointEntries).countDocuments({ kind: { $in: [...BONUS_KINDS] } })).toBe(8);
  });

  it('pays the cycle bonuses once the cycle has ended, and redraws them when the anchor moves', async () => {
    const f = await fixture();
    await doTheWeek(f);
    // Cycle 0 ends on Sunday 11 October.
    await f.reconcileAt('2026-10-11T21:00:00.000Z');
    expect(await bonuses(f)).toEqual(WEEK_BONUSES);
    await f.reconcileAt('2026-10-12T01:00:00.000Z');
    expect(await bonuses(f)).toEqual(
      [
        ...WEEK_BONUSES,
        'bonus_cycle_done p1 2026-09-14 20',
        'bonus_cycle_done p2 2026-09-14 20',
        'bonus_cycle_ontime p1 2026-09-14 10',
        'bonus_cycle_ontime p2 2026-09-14 10',
      ].sort(),
    );

    // The anchor moves one week back: the same work now sits in the cycle that starts on 7 September.
    expect((await f.at('2026-10-12T02:00:00.000Z', 'PATCH', '/api/settings', { cycleAnchorDate: '2026-09-07' })).statusCode).toBe(200);
    const result = await f.reconcileAt('2026-10-12T03:00:00.000Z');
    expect(result).toMatchObject({ bonusesCreated: 4, bonusesRemoved: 4, bonusChangesTotal: 8 });
    expect(result.bonusChanges.slice(0, 4).every((change) => change.change === 'removed' && change.key.includes('2026-09-14'))).toBe(true);
    expect(result.bonusChanges.slice(4).every((change) => change.change === 'created' && change.key.includes('2026-09-07'))).toBe(true);
    expect(await bonuses(f)).toEqual(
      [
        ...WEEK_BONUSES,
        'bonus_cycle_done p1 2026-09-07 20',
        'bonus_cycle_done p2 2026-09-07 20',
        'bonus_cycle_ontime p1 2026-09-07 10',
        'bonus_cycle_ontime p2 2026-09-07 10',
      ].sort(),
    );
  });
});

describe('reading', () => {
  it('counts only executions in `executions`, reports the bonuses in `bonusPoints`, and includes the last day of a period', async () => {
    const f = await fixture();
    await doTheWeek(f);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    const balances = async (query: string) =>
      (await f.t.app.inject({ method: 'GET', url: `/api/points/balances${query}` })).json<PointsBalancesResponse>().balances;
    const p1 = f.p1._id.toHexString();

    expect((await balances('')).find((b) => b.personId === p1)).toEqual({ personId: p1, points: 3 + 8, executions: 1, bonusPoints: 8 });
    // The bonus is dated on the last day of the week: a range that stops before it does not include it.
    expect((await balances('?from=2026-09-14&to=2026-09-19')).find((b) => b.personId === p1)).toMatchObject({ points: 3, executions: 1, bonusPoints: 0 });
    expect((await balances('?from=2026-09-14&to=2026-09-20')).find((b) => b.personId === p1)).toMatchObject({ points: 11, bonusPoints: 8 });

    const entries = (await f.t.app.inject({ method: 'GET', url: `/api/points/entries?personId=${p1}&from=2026-09-14&to=2026-09-20` })).json<PointsEntriesResponse>().entries;
    const bonus = entries.find((entry) => entry.kind === 'bonus_week_ontime')!;
    expect(bonus).toMatchObject({ amount: 3, date: '2026-09-20', weekStart: '2026-09-14', periodStart: '2026-09-14', occurrenceId: null, taskId: null, titleSnapshot: '', source: 'recompute' });
    expect(entries.find((entry) => entry.kind === 'execution')).toMatchObject({ periodStart: null });
  });
});

describe('statistics reset', () => {
  it('deletes the bonuses of the periods that ended before the boundary, with the executions', async () => {
    const f = await fixture();
    await doTheWeek(f);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    const res = await f.call('DELETE', '/api/stats?before=2026-09-21');
    expect(res.statusCode, res.body).toBe(200);
    // Two executions and four bonuses.
    expect(res.json()).toMatchObject({ removedPointEntries: 6 });
    expect(await bonuses(f)).toEqual([]);
    // A restart finds nothing to award again.
    const restart = await captureWrites(f.t, () => reconcilePointsSafely(f.t.systemCtx(), 'startup'));
    expect(restart.writes).toEqual([]);
    expect(await bonuses(f)).toEqual([]);
  });

  it('keeps the bonuses of a period that straddles the boundary until the next run evaluates what remains', async () => {
    const f = await fixture();
    await doTheWeek(f);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    const res = await f.call('DELETE', '/api/stats?before=2026-09-17');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toMatchObject({ removedPointEntries: 2 });
    expect(await bonuses(f)).toEqual(WEEK_BONUSES);
    // Both tasks were purged, so no person has a set in that week any more.
    const result = await f.reconcileAt('2026-09-21T02:00:00.000Z');
    expect(result).toMatchObject({ bonusesRemoved: 4 });
    expect(await bonuses(f)).toEqual([]);
  });

  it('removes every bonus when the statistics start over', async () => {
    const f = await fixture();
    await doTheWeek(f);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    const res = await f.call('DELETE', '/api/stats');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toMatchObject({ removedPointEntries: 6 });
    expect(await bonuses(f)).toEqual([]);
  });
});

describe('transfer', () => {
  it('rebuilds the same ledger after an import, because the schedule travels in the settings', async () => {
    const f = await fixture();
    await doTheWeek(f);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    const before = await bonuses(f);
    expect(before).toEqual(WEEK_BONUSES);

    const file = (await f.t.app.inject({ method: 'GET', url: '/api/export/json' })).json<ExportFile>();
    expect(file.schemaVersion).toBe(4);
    expect(file.collections.settings[0]!.bonusSchedule).toEqual([{ from: '2026-09-14', ...AMOUNTS }]);
    const res = await f.call('POST', '/api/import/json?mode=replace&confirm=true', file as unknown as Record<string, unknown>);
    expect(res.statusCode, res.body).toBe(200);
    expect(await bonuses(f)).toEqual(before);
    const audit = await auditOf(f.t);
    expect(audit.at(-1)!.meta).toMatchObject({ trigger: 'import', bonusesCreated: 4, bonusesRemoved: 0 });
  });

  it('rebuilds an older file, which has no schedule, without bonuses', async () => {
    const f = await fixture();
    await doTheWeek(f);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    const legacy = structuredClone((await f.t.app.inject({ method: 'GET', url: '/api/export/json' })).json<ExportFile>());
    legacy.schemaVersion = 3 as never;
    delete legacy.collections.settings[0]!.bonusSchedule;
    const res = await f.call('POST', '/api/import/json?mode=replace&confirm=true', legacy as unknown as Record<string, unknown>);
    expect(res.statusCode, res.body).toBe(200);
    expect(await bonuses(f)).toEqual([]);
    expect((await f.t.app.inject({ method: 'GET', url: '/api/settings' })).json()).toMatchObject({ bonusSchedule: [] });
  });
});

describe('the statistics reset floor', () => {
  it('keeps a purged week from paying out on what remains, for an item that was dragged forward out of it', async () => {
    const f = await fixture();
    const monday = await f.occurrence('2026-09-14');
    const tuesday = await f.occurrence('2026-09-15');
    expect((await f.at('2026-09-15T07:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, complete, f.p2)).statusCode).toBe(200);
    // Person 1 never did Monday's task; it was dragged to 22 September and finished there.
    expect((await f.at('2026-09-21T08:00:00.000Z', 'PATCH', `/api/occurrences/${monday}`, { action: 'reschedule', date: '2026-09-22' })).statusCode).toBe(200);
    expect((await f.at('2026-09-22T08:00:00.000Z', 'PATCH', `/api/occurrences/${monday}`, complete, f.p1)).statusCode).toBe(200);

    const res = await f.at('2026-09-22T09:00:00.000Z', 'DELETE', '/api/stats?before=2026-09-21');
    expect(res.statusCode, res.body).toBe(200);
    expect((await f.t.db.collection(COLLECTIONS.settings).findOne({}))!.bonusFloor).toBe('2026-09-21');
    const reset = (await f.t.db.collection(COLLECTIONS.auditLog).find({ entity: 'settings', action: 'reset' }).toArray()).at(-1)!;
    expect(reset.after).toMatchObject({ bonusFloor: '2026-09-21' });

    // Only the dragged item survived the purge. The week it was planned in began before the boundary, so it pays nothing.
    await f.reconcileAt('2026-09-22T10:00:00.000Z');
    expect(await bonuses(f)).toEqual([]);

    // Without the floor the same history would award the purged week.
    await f.t.db.collection(COLLECTIONS.settings).updateOne({}, { $unset: { bonusFloor: '' } });
    await f.reconcileAt('2026-09-22T11:00:00.000Z');
    expect(await bonuses(f)).toEqual(['bonus_week_done p1 2026-09-14 5']);
  });

  it('applies the floor when the statistics start over, and only moves it forward', async () => {
    const f = await fixture();
    const monday = await f.occurrence('2026-09-14');
    const tuesday = await f.occurrence('2026-09-15');
    expect((await f.at('2026-09-15T07:00:00.000Z', 'PATCH', `/api/occurrences/${tuesday}`, complete, f.p2)).statusCode).toBe(200);
    // Person 1 dragged Monday's task forward to 25 September instead of doing it.
    expect((await f.at('2026-09-21T08:00:00.000Z', 'PATCH', `/api/occurrences/${monday}`, { action: 'reschedule', date: '2026-09-25' })).statusCode).toBe(200);
    await f.reconcileAt('2026-09-21T09:00:00.000Z');
    expect(await bonuses(f)).toEqual(WEEK_BONUSES.filter((entry) => entry.includes(' p2 ')));

    // Starting over deletes the past, reopens the rest and sets the floor to today.
    expect((await f.at('2026-09-22T08:00:00.000Z', 'DELETE', '/api/stats')).statusCode).toBe(200);
    expect((await f.t.db.collection(COLLECTIONS.settings).findOne({}))!.bonusFloor).toBe('2026-09-22');
    // The dragged item is finished late; the week it was planned in began before the floor.
    expect((await f.at('2026-09-25T08:00:00.000Z', 'PATCH', `/api/occurrences/${monday}`, complete, f.p1)).statusCode).toBe(200);
    await f.reconcileAt('2026-09-25T09:00:00.000Z');
    expect(await bonuses(f)).toEqual([]);

    // Without the floor the surviving item would pay out the week that was cleared.
    await f.t.db.collection(COLLECTIONS.settings).updateOne({}, { $unset: { bonusFloor: '' } });
    await f.reconcileAt('2026-09-25T10:00:00.000Z');
    expect(await bonuses(f)).toEqual(['bonus_week_done p1 2026-09-14 5']);

    // A later purge before an earlier day never moves the floor back.
    await f.t.db.collection(COLLECTIONS.settings).updateOne({}, { $set: { bonusFloor: '2026-09-22' } });
    expect((await f.at('2026-09-26T08:00:00.000Z', 'DELETE', '/api/stats?before=2026-09-10')).statusCode).toBe(200);
    expect((await f.t.db.collection(COLLECTIONS.settings).findOne({}))!.bonusFloor).toBe('2026-09-22');
  });
});

describe('writing the bonuses', () => {
  it('still audits what steps 1 to 3 changed when the bonus step fails, and rethrows the failure', async () => {
    const f = await fixture();
    await doTheWeek(f);
    const col = f.t.db.collection(COLLECTIONS.pointEntries);
    const now = new Date('2026-09-21T01:00:00.000Z');
    // Drift for step 3 to repair: an execution entry without an occurrence.
    await col.insertOne({
      _id: new ObjectId(),
      key: `execution:${new ObjectId().toHexString()}`,
      kind: 'execution',
      personId: f.p1._id,
      amount: 2,
      date: now,
      weekStart: now,
      occurrenceId: null,
      taskId: null,
      titleSnapshot: 'Verdwaald',
      source: 'live',
      createdAt: now,
      updatedAt: now,
    });
    // An anchor that is not a Monday makes the cycle calculation of step 4 throw.
    await f.t.db.collection(COLLECTIONS.settings).updateOne({}, { $set: { cycleAnchorDate: '2026-09-15' } });
    f.t.clock.set(now);
    await expect(reconcilePoints(f.t.systemCtx(), 'nightly')).rejects.toThrow(RangeError);
    const audit = await auditOf(f.t);
    expect(audit).toHaveLength(1);
    expect(audit[0]!.meta).toMatchObject({ removed: 1, bonusesCreated: 0, bonusesRemoved: 0 });
    expect(await col.countDocuments({ titleSnapshot: 'Verdwaald' })).toBe(0);
  });

  it('only counts the writes that happened: a delete that missed its compare-and-set and a duplicate key are left out', async () => {
    const f = await fixture();
    await doTheWeek(f);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    const entries = await findBonusPointEntries(f.t.db);
    const [stale, gone, kept] = entries as [PointEntryDoc, PointEntryDoc, PointEntryDoc];
    // Somebody changed the first entry after it was read: the compare-and-set misses.
    await f.t.db.collection(COLLECTIONS.pointEntries).updateOne({ _id: stale._id }, { $set: { amount: 99 } });
    const fresh = {
      personId: f.p1._id,
      amount: 5,
      date: new Date('2026-08-30T22:00:00.000Z'),
      weekStart: new Date('2026-08-23T22:00:00.000Z'),
      periodStart: new Date('2026-08-23T22:00:00.000Z'),
      occurrenceId: null,
      taskId: null,
      titleSnapshot: '',
    };
    const newKey = `bonus_week_done:${f.p1._id.toHexString()}:2026-08-24`;
    const applied = await applyBonusEntryChanges(f.t.systemCtx(), {
      deletes: [stale, gone],
      inserts: [
        { key: kept.key, kind: kept.kind, fields: fresh },
        { key: newKey, kind: 'bonus_week_done', fields: fresh },
      ],
    });
    expect(applied.error).toBeUndefined();
    expect(applied.removed).toEqual([gone.key]);
    expect(applied.created).toEqual([newKey]);
    expect(await f.t.db.collection(COLLECTIONS.pointEntries).countDocuments({ _id: stale._id })).toBe(1);
    expect((await f.t.db.collection(COLLECTIONS.pointEntries).findOne({ key: kept.key }))!.amount).toBe(kept.amount);
  });

  it('refuses an import whose bonus schedule holds a row that has not started yet', async () => {
    const f = await fixture();
    await doTheWeek(f);
    await f.reconcileAt('2026-09-21T01:00:00.000Z');
    const file = (await f.t.app.inject({ method: 'GET', url: '/api/export/json' })).json<ExportFile>();
    file.collections.settings[0]!.bonusSchedule = [{ from: '2026-09-14', ...AMOUNTS }, { from: '2026-09-22', ...AMOUNTS }];
    const capture = await captureWrites(f.t, () => f.call('POST', '/api/import/json?mode=replace&confirm=true', file as unknown as Record<string, unknown>));
    expect(capture.result.statusCode).toBe(400);
    expect(capture.result.json<{ details: { field: string; message: string }[] }>().details).toContainEqual({
      field: 'collections.settings.0.bonusSchedule.1.from',
      message: 'bonus_schedule_in_future',
    });
    expect(capture.writes).toEqual([]);
    // A row that starts today is accepted.
    file.collections.settings[0]!.bonusSchedule = [{ from: '2026-09-21', ...AMOUNTS }];
    expect((await f.call('POST', '/api/import/json?mode=replace&confirm=true', file as unknown as Record<string, unknown>)).statusCode).toBe(200);
  });
});
