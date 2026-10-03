import type { LightMyRequestResponse } from 'fastify';
import { fromDayKey, type OccurrenceView, type PointsProgressResponse } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { insertPointEntry } from '../src/data/points.ts';
import { getSettings } from '../src/data/settings.ts';
import type { UserDoc } from '../src/data/users.ts';
import { reconcilePoints } from '../src/domain/points.ts';
import { captureWrites, expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

/**
 * requirements 4.12: GET /api/points/progress answers how far one person is towards the goal of the current week or cycle.
 * Monday 14 Sep 2026 is the first day of cycle 0 (14 Sep to 11 Oct) and the clock starts on Wednesday 16 Sep.
 * Stofzuigen (3 minutes, 3 points) is planned on Monday for person 1, Tuesday for person 2 and Wednesday for
 * nobody; Dweilen (1 minute, 1 point) on Thursday of week 0 and Monday of week 1, both for person 1.
 */
const apps: TestApp[] = [];

afterEach(async () => {
  for (const app of apps.splice(0)) await app.close();
});

interface Fixture {
  t: TestApp;
  p1: UserDoc;
  p2: UserDoc;
  /** An active person that nothing is planned for. */
  guest: string;
  occurrence: (date: string, task?: 'stofzuigen' | 'dweilen') => Promise<string>;
  call: (method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', url: string, payload?: Record<string, unknown>, actor?: UserDoc) => Promise<LightMyRequestResponse>;
  /** Moves the clock and performs one request as the person. */
  at: (now: string, method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', url: string, payload?: Record<string, unknown>, actor?: UserDoc) => Promise<LightMyRequestResponse>;
  progress: (person: UserDoc | string, period: 'week' | 'cycle') => Promise<PointsProgressResponse>;
}

async function fixture(): Promise<Fixture> {
  // The cycle is generated on Monday morning, like the nightly job does; then the clock moves to Wednesday.
  const t = await createTestApp({ now: '2026-09-14T06:00:00.000Z' });
  apps.push(t);
  const [p1, p2] = await seededUsers(t);
  const call: Fixture['call'] = (method, url, payload, actor = p1) =>
    t.app.inject({ method, url, headers: asProfile(actor), ...(payload ? { payload } : {}) });
  const created = await call('POST', '/api/users', { name: 'Logé', color: '#16a34a' });
  expect(created.statusCode, created.body).toBe(201);
  const guest = created.json<{ _id: string }>()._id;

  const makeTask = async (name: string, roomName: string, durationMinutes: number) => {
    const res = await call('POST', '/api/tasks', { name, roomId: (await seededRoom(t, roomName))._id.toHexString(), intervalKey: '1w', durationMinutes });
    expect(res.statusCode, res.body).toBe(201);
    return res.json<{ _id: string }>()._id;
  };
  const tasks = { stofzuigen: await makeTask('Stofzuigen', 'Woonkamer', 3), dweilen: await makeTask('Dweilen', 'Keuken', 1) };
  const plan = (await findActivePlan(t.db))!;
  const slots = [
    { taskId: tasks.stofzuigen, weekIndex: 0, weekday: 1, assigneeId: p1._id.toHexString() },
    { taskId: tasks.stofzuigen, weekIndex: 0, weekday: 2, assigneeId: p2._id.toHexString() },
    { taskId: tasks.stofzuigen, weekIndex: 0, weekday: 3, assigneeId: null },
    { taskId: tasks.dweilen, weekIndex: 0, weekday: 4, assigneeId: p1._id.toHexString() },
    { taskId: tasks.dweilen, weekIndex: 1, weekday: 1, assigneeId: p1._id.toHexString() },
  ];
  expect((await call('PUT', `/api/cycle-plans/${plan._id.toHexString()}/slots`, { slots })).statusCode).toBe(200);
  expect((await call('POST', '/api/jobs/nightly')).statusCode).toBe(200);
  t.clock.set('2026-09-16T08:00:00.000Z');

  const occurrence: Fixture['occurrence'] = async (date, task = 'stofzuigen') => {
    const res = await t.app.inject({ method: 'GET', url: `/api/occurrences?from=${date}&to=${date}` });
    return res.json<OccurrenceView[]>().find((o) => o.taskId === tasks[task])!._id;
  };
  const at: Fixture['at'] = (now, method, url, payload, actor) => {
    t.clock.set(now);
    return call(method, url, payload, actor);
  };
  const progress: Fixture['progress'] = async (person, period) => {
    const personId = typeof person === 'string' ? person : person._id.toHexString();
    const res = await t.app.inject({ method: 'GET', url: `/api/points/progress?personId=${personId}&period=${period}` });
    expect(res.statusCode, res.body).toBe(200);
    return res.json<PointsProgressResponse>();
  };
  return { t, p1, p2, guest, occurrence, call, at, progress };
}

const complete = { action: 'complete' } as const;

describe('GET /api/points/progress', () => {
  it('uses the points planned for the person as the goal, per week and per cycle, and counts what they earned', async () => {
    const f = await fixture();
    // Week 14 to 20 September: person 1 has Stofzuigen (3) and Dweilen (1); the cycle adds Dweilen of week 2 (1).
    expect(await f.progress(f.p1, 'week')).toEqual({
      personId: f.p1._id.toHexString(),
      period: 'week',
      start: '2026-09-14',
      end: '2026-09-20',
      earnedPoints: 0,
      goalPoints: 4,
      goalSource: 'automatic',
      percent: 0,
      currencyCode: 'EUR',
      centsPerPoint: 0,
      money: null,
    });
    expect(await f.progress(f.p1, 'cycle')).toMatchObject({ period: 'cycle', start: '2026-09-14', end: '2026-10-11', goalPoints: 5, earnedPoints: 0, percent: 0 });
    expect(await f.progress(f.p2, 'week')).toMatchObject({ goalPoints: 3 });
    expect(await f.progress(f.p2, 'cycle')).toMatchObject({ goalPoints: 3 });

    expect((await f.call('PATCH', `/api/occurrences/${await f.occurrence('2026-09-14')}`, complete)).statusCode).toBe(200);
    expect(await f.progress(f.p1, 'week')).toMatchObject({ earnedPoints: 3, goalPoints: 4, percent: 75 });
    expect(await f.progress(f.p1, 'cycle')).toMatchObject({ earnedPoints: 3, goalPoints: 5, percent: 60 });
    expect(await f.progress(f.p2, 'week')).toMatchObject({ earnedPoints: 0, percent: 0 });

    expect((await f.call('PATCH', `/api/occurrences/${await f.occurrence('2026-09-17', 'dweilen')}`, complete)).statusCode).toBe(200);
    expect(await f.progress(f.p1, 'week')).toMatchObject({ earnedPoints: 4, goalPoints: 4, percent: 100 });
  });

  it('says there is no goal when nothing is planned for the person, and leaves out skipped work', async () => {
    const f = await fixture();
    expect(await f.progress(f.guest, 'week')).toMatchObject({ earnedPoints: 0, goalPoints: null, goalSource: 'automatic', percent: 0, money: null });
    expect(await f.progress(f.guest, 'cycle')).toMatchObject({ goalPoints: null, percent: 0 });

    // Skipped work cannot be earned, so it leaves the goal: Dweilen of Thursday goes, Stofzuigen stays.
    const skip = await f.call('PATCH', `/api/occurrences/${await f.occurrence('2026-09-17', 'dweilen')}`, { action: 'skip', reason: 'Geen tijd' });
    expect(skip.statusCode, skip.body).toBe(200);
    expect(await f.progress(f.p1, 'week')).toMatchObject({ goalPoints: 3, goalSource: 'automatic' });
  });

  it('follows the owner of the work: a take-over inside the week moves it, and the owner of a finished week keeps it', async () => {
    const f = await fixture();
    // Inside the planned week the actor becomes the assignee and so the owner (ADR-0012).
    const tuesday = await f.occurrence('2026-09-15');
    expect((await f.call('PATCH', `/api/occurrences/${tuesday}`, { action: 'complete', takeOver: true })).statusCode).toBe(200);
    expect(await f.progress(f.p1, 'week')).toMatchObject({ goalPoints: 7, earnedPoints: 3 });
    expect(await f.progress(f.p2, 'week')).toMatchObject({ goalPoints: null, earnedPoints: 0 });

    // After the week ended the owner is frozen: person 1 takes over Tuesday's overdue work of person 2 and it stays with person 2.
    const g = await fixture();
    const gTuesday = await g.occurrence('2026-09-15');
    expect((await g.at('2026-09-15T08:00:00.000Z', 'PATCH', `/api/occurrences/${gTuesday}`, complete, g.p2)).statusCode).toBe(200);
    expect((await g.at('2026-09-22T08:00:00.000Z', 'PATCH', `/api/occurrences/${gTuesday}`, { action: 'uncomplete' }, g.p2)).statusCode).toBe(200);
    expect((await g.at('2026-09-23T08:00:00.000Z', 'PATCH', `/api/occurrences/${gTuesday}`, { action: 'complete', takeOver: true }, g.p1)).statusCode).toBe(200);
    // The cycle still holds the work for person 2 (its owner); person 1 did it and earned its points, but it is not in their goal (3 + 1 + 1).
    expect(await g.progress(g.p2, 'cycle')).toMatchObject({ goalPoints: 3, earnedPoints: 0, percent: 0 });
    expect(await g.progress(g.p1, 'cycle')).toMatchObject({ goalPoints: 5, earnedPoints: 3, percent: 60 });
  });

  it('keeps work that somebody else did in the goal of its owner, and credits the person who did it', async () => {
    const f = await fixture();
    const tuesday = await f.occurrence('2026-09-15');
    const done = await f.call('PATCH', `/api/occurrences/${tuesday}`, { action: 'complete', completedBy: f.p1._id.toHexString() });
    expect(done.statusCode, done.body).toBe(200);
    expect(await f.progress(f.p2, 'week')).toMatchObject({ goalPoints: 3, earnedPoints: 0, percent: 0 });
    expect(await f.progress(f.p1, 'week')).toMatchObject({ goalPoints: 4, earnedPoints: 3, percent: 75 });
  });

  it('takes the explicit goal of the administrator over, switches it off with 0 and keeps the other period automatic', async () => {
    const f = await fixture();
    expect((await f.call('PATCH', `/api/occurrences/${await f.occurrence('2026-09-14')}`, complete)).statusCode).toBe(200);
    expect((await f.call('PATCH', '/api/settings', { rewardGoals: { weekPoints: 6, cyclePoints: null } })).statusCode).toBe(200);
    expect(await f.progress(f.p1, 'week')).toMatchObject({ goalPoints: 6, goalSource: 'explicit', earnedPoints: 3, percent: 50 });
    expect(await f.progress(f.p1, 'cycle')).toMatchObject({ goalPoints: 5, goalSource: 'automatic', percent: 60 });
    // An explicit goal also applies to a person nothing is planned for.
    expect(await f.progress(f.guest, 'week')).toMatchObject({ goalPoints: 6, goalSource: 'explicit', percent: 0 });

    // A goal of 0 switches the meter off for the period.
    expect((await f.call('PATCH', '/api/settings', { rewardGoals: { weekPoints: 0, cyclePoints: 2 } })).statusCode).toBe(200);
    expect(await f.progress(f.p1, 'week')).toMatchObject({ goalPoints: null, goalSource: 'explicit', percent: 0, earnedPoints: 3 });
    // More earned than the goal is capped at 100%, while the points stay exact.
    expect(await f.progress(f.p1, 'cycle')).toMatchObject({ goalPoints: 2, goalSource: 'explicit', percent: 100, earnedPoints: 3 });
  });

  it('shows money when a point is worth something: what is earned and what the goal is worth', async () => {
    const f = await fixture();
    expect((await f.call('PATCH', '/api/settings', { currencyCode: 'USD', centsPerPoint: 25 })).statusCode).toBe(200);
    expect((await f.call('PATCH', `/api/occurrences/${await f.occurrence('2026-09-14')}`, complete)).statusCode).toBe(200);
    expect(await f.progress(f.p1, 'week')).toMatchObject({
      earnedPoints: 3,
      goalPoints: 4,
      currencyCode: 'USD',
      centsPerPoint: 25,
      money: { earned: 75, goal: 100 },
    });
    expect(await f.progress(f.guest, 'week')).toMatchObject({ goalPoints: null, money: { earned: 0, goal: null } });
  });

  it('counts the bonuses of ended weeks in the cycle, and caps the percentage', async () => {
    const f = await fixture();
    expect((await f.call('PATCH', '/api/settings', { periodBonuses: { weekDone: 5, weekOnTime: 0, cycleDone: 0, cycleOnTime: 0 } })).statusCode).toBe(200);
    expect((await f.at('2026-09-14T07:00:00.000Z', 'PATCH', `/api/occurrences/${await f.occurrence('2026-09-14')}`, complete)).statusCode).toBe(200);
    expect((await f.at('2026-09-17T07:00:00.000Z', 'PATCH', `/api/occurrences/${await f.occurrence('2026-09-17', 'dweilen')}`, complete)).statusCode).toBe(200);

    // While the week runs nothing is paid.
    expect(await f.progress(f.p1, 'cycle')).toMatchObject({ earnedPoints: 4 });

    // Monday 21 September, 03:00 local time: week 1 is finalised, and its bonus is dated on Sunday 20 September.
    f.t.clock.set('2026-09-21T01:00:00.000Z');
    await reconcilePoints(f.t.systemCtx(), 'nightly');
    // The new week has earned nothing yet; the bonus belongs to the week that ended and to the cycle.
    expect(await f.progress(f.p1, 'week')).toMatchObject({ start: '2026-09-21', end: '2026-09-27', earnedPoints: 0, goalPoints: 1, percent: 0 });
    // 3 + 1 + 5 bonus points against a goal of 5.
    expect(await f.progress(f.p1, 'cycle')).toMatchObject({ earnedPoints: 9, goalPoints: 5, percent: 100 });
  });

  it('does not let a redemption lower the progress', async () => {
    const f = await fixture();
    expect((await f.call('PATCH', `/api/occurrences/${await f.occurrence('2026-09-14')}`, complete)).statusCode).toBe(200);
    const redeemed = await f.call('POST', '/api/points/redemptions', { points: 2, note: 'Ijsje' });
    expect(redeemed.statusCode, redeemed.body).toBe(201);
    expect(await f.progress(f.p1, 'week')).toMatchObject({ earnedPoints: 3, percent: 75 });
    const balances = await f.t.app.inject({ method: 'GET', url: '/api/points/balances' });
    expect(balances.json<{ balances: { personId: string; points: number }[] }>().balances.find((b) => b.personId === f.p1._id.toHexString())).toMatchObject({ points: 1 });
  });

  it('puts the week and the cycle on local midnight across the end of daylight saving time', async () => {
    const f = await fixture();
    // Thursday 22 October 2026; clocks go back on Sunday 25 October, so the week of 19 to 25 October has 169 hours.
    f.t.clock.set('2026-10-22T08:00:00.000Z');
    const earn = async (day: string, amount: number) => {
      const occurrenceId = new ObjectId();
      const result = await insertPointEntry(
        f.t.systemCtx(),
        `execution:${occurrenceId.toHexString()}`,
        'execution',
        { personId: f.p2._id, amount, date: fromDayKey(day), weekStart: fromDayKey(day), occurrenceId, taskId: null, titleSnapshot: 'Taak' },
        'live',
        { occurrenceId, reason: 'complete' },
      );
      expect(result.inserted).toBe(true);
    };
    await earn('2026-10-18', 100); // the Sunday before: another week
    await earn('2026-10-19', 1); // Monday, local midnight 22:00Z the day before
    await earn('2026-10-25', 2); // Sunday, local midnight 22:00Z the day before; the last day of the week
    await earn('2026-10-26', 4); // the next Monday, local midnight 23:00Z the day before: the next week

    const week = await f.progress(f.p2, 'week');
    expect(week).toMatchObject({ start: '2026-10-19', end: '2026-10-25', earnedPoints: 3 });
    const cycle = await f.progress(f.p2, 'cycle');
    expect(cycle).toMatchObject({ start: '2026-10-12', end: '2026-11-08', earnedPoints: 107 });
    expect(fromDayKey('2026-10-26').toISOString()).toBe('2026-10-25T23:00:00.000Z');
  });

  it('reads the period of today in the household timezone, also around local midnight', async () => {
    const f = await fixture();
    // Sunday 20 September 22:30Z is already Monday 21 September 00:30 in Amsterdam: a new week.
    f.t.clock.set('2026-09-20T22:30:00.000Z');
    expect(await f.progress(f.p1, 'week')).toMatchObject({ start: '2026-09-21', end: '2026-09-27' });
    f.t.clock.set('2026-09-20T21:30:00.000Z');
    expect(await f.progress(f.p1, 'week')).toMatchObject({ start: '2026-09-14', end: '2026-09-20' });
  });

  it('validates the person and the period, and needs no profile', async () => {
    const f = await fixture();
    const get = (query: string) => f.t.app.inject({ method: 'GET', url: `/api/points/progress${query}` });
    for (const query of ['', '?period=week', `?personId=${f.guest}`, `?personId=${f.guest}&period=month`, '?personId=nope&period=week']) {
      const res = await get(query);
      expect(res.statusCode, query).toBe(400);
      expect(res.json()).toMatchObject({ code: 'validation_error' });
    }
    expect((await get(`?personId=${f.guest}&period=cycle`)).statusCode).toBe(200);
  });

  it('writes and audits nothing', async () => {
    const f = await fixture();
    const read = await captureWrites(f.t, () => f.progress(f.p1, 'cycle'));
    expect(read.writes).toEqual([]);
    expect(read.auditInserts).toBe(0);
  });
});

describe('rewardGoals in the settings', () => {
  it('are returned as automatic by default, and an administrator sets, changes and clears them with one audit entry each', async () => {
    const f = await fixture();
    const settings = () => f.t.app.inject({ method: 'GET', url: '/api/settings' }).then((res) => res.json<{ rewardGoals: unknown }>());
    expect((await settings()).rewardGoals).toEqual({ weekPoints: null, cyclePoints: null });

    const set = await expectAudited(f.t, () => f.call('PATCH', '/api/settings', { rewardGoals: { weekPoints: 12, cyclePoints: 40 } }), { entity: 'settings', action: 'update', count: 1 });
    expect(set.result.statusCode, set.result.body).toBe(200);
    expect(set.entries[0]).toMatchObject({ after: { rewardGoals: { weekPoints: 12, cyclePoints: 40 } } });
    expect((await settings()).rewardGoals).toEqual({ weekPoints: 12, cyclePoints: 40 });

    const part = await expectAudited(f.t, () => f.call('PATCH', '/api/settings', { rewardGoals: { weekPoints: 12, cyclePoints: null } }), { entity: 'settings', action: 'update', count: 1 });
    expect(part.entries[0]).toMatchObject({ before: { rewardGoals: { cyclePoints: 40 } }, after: { rewardGoals: { cyclePoints: null } } });

    const cleared = await f.call('PATCH', '/api/settings', { rewardGoals: { weekPoints: null, cyclePoints: null } });
    expect(cleared.statusCode, cleared.body).toBe(200);
    expect((await settings()).rewardGoals).toEqual({ weekPoints: null, cyclePoints: null });
  });

  it('write and audit nothing when they do not change, also while none were ever set', async () => {
    const f = await fixture();
    const same = await captureWrites(f.t, () => f.call('PATCH', '/api/settings', { rewardGoals: { weekPoints: null, cyclePoints: null } }));
    expect(same.result.statusCode, same.result.body).toBe(200);
    expect(same.writes).toEqual([]);
    expect(same.auditInserts).toBe(0);
    expect((await getSettings(f.t.db))!.rewardGoals).toBeUndefined();

    expect((await f.call('PATCH', '/api/settings', { rewardGoals: { weekPoints: 5, cyclePoints: null } })).statusCode).toBe(200);
    const again = await captureWrites(f.t, () => f.call('PATCH', '/api/settings', { rewardGoals: { weekPoints: 5, cyclePoints: null } }));
    expect(again.writes).toEqual([]);
    expect(again.auditInserts).toBe(0);
  });

  it('can only be changed by an administrator, and only to whole numbers from 0 to 100000 or null', async () => {
    const f = await fixture();
    const member = await captureWrites(f.t, () => f.call('PATCH', '/api/settings', { rewardGoals: { weekPoints: 5, cyclePoints: 5 } }, f.p2));
    expect(member.result.statusCode).toBe(403);
    expect(member.result.json()).toMatchObject({ code: 'permission_denied' });
    expect(member.writes).toEqual([]);

    const anonymous = await f.t.app.inject({ method: 'PATCH', url: '/api/settings', payload: { rewardGoals: { weekPoints: 5, cyclePoints: 5 } } });
    expect(anonymous.statusCode).toBe(400);

    for (const bad of [
      { weekPoints: -1, cyclePoints: null },
      { weekPoints: null, cyclePoints: 100001 },
      { weekPoints: 1.5, cyclePoints: null },
      { weekPoints: '5', cyclePoints: null },
      { weekPoints: 5 },
      {},
    ]) {
      const res = await f.call('PATCH', '/api/settings', { rewardGoals: bad });
      expect(res.statusCode, JSON.stringify(bad)).toBe(400);
      expect(res.json()).toMatchObject({ code: 'validation_error' });
    }
    const edge = await f.call('PATCH', '/api/settings', { rewardGoals: { weekPoints: 0, cyclePoints: 100000 } });
    expect(edge.statusCode, edge.body).toBe(200);
    expect(await f.t.db.collection(COLLECTIONS.auditLog).countDocuments({ entity: 'settings', action: 'update' })).toBe(1);
  });
});
