import { fromDayKey, type PointsBalancesResponse, type PointsEntriesResponse } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { insertPointEntry } from '../src/data/points.ts';
import type { UserDoc } from '../src/data/users.ts';
import { asProfile, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

/**
 * ADR-0011: GET /api/points/balances and /api/points/entries read the ledger. The entries are
 * written through the data layer so the tests do not depend on the live paths.
 */
let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
let guest: string; // active, no entries
let former: string; // inactive, with entries
let gone: string; // inactive, without entries
const ids: ObjectId[] = [];

const get = (url: string) => t.app.inject({ method: 'GET', url });

async function addEntry(personId: ObjectId | string, date: string, amount: number, title = 'Taak'): Promise<ObjectId> {
  const occurrenceId = new ObjectId();
  const result = await insertPointEntry(
    t.systemCtx(),
    `execution:${occurrenceId.toHexString()}`,
    'execution',
    {
      personId: new ObjectId(personId),
      amount,
      date: fromDayKey(date),
      weekStart: fromDayKey(date),
      occurrenceId,
      taskId: null,
      titleSnapshot: title,
    },
    'live',
    { occurrenceId, reason: 'complete' },
  );
  if (!result.inserted) throw new Error('duplicate key');
  ids.push(result.doc._id);
  return result.doc._id;
}

beforeAll(async () => {
  t = await createTestApp();
  [p1, p2] = await seededUsers(t);
  const make = async (name: string, active: boolean) => {
    const created = await t.app.inject({
      method: 'POST',
      url: '/api/users',
      headers: asProfile(p1),
      payload: { name, color: '#16a34a' },
    });
    expect(created.statusCode, created.body).toBe(201);
    const id = created.json<{ _id: string }>()._id;
    if (!active) {
      const patched = await t.app.inject({ method: 'PATCH', url: `/api/users/${id}`, headers: asProfile(p1), payload: { active: false } });
      expect(patched.statusCode, patched.body).toBe(200);
    }
    return id;
  };
  guest = await make('Logé', true);
  former = await make('Oud-bewoner', false);
  gone = await make('Vertrokken', false);
  await addEntry(p1._id, '2026-09-01', 3);
  await addEntry(p1._id, '2026-09-14', 5);
  await addEntry(p1._id, '2026-09-14', 2);
  await addEntry(p1._id, '2026-09-20', 1);
  await addEntry(p2._id, '2026-09-15', 10);
  await addEntry(former, '2026-08-30', 4);
});

afterAll(async () => {
  await t.close();
});

describe('GET /api/points/balances', () => {
  it('sums the whole ledger without a range, in the order of the user list, including inactive users with entries', async () => {
    const res = await get('/api/points/balances');
    expect(res.statusCode, res.body).toBe(200);
    const body = res.json<PointsBalancesResponse>();
    expect(body.from).toBeNull();
    expect(body.to).toBeNull();
    // Persoon 1, Persoon 2, Logé (active, 0), Oud-bewoner (inactive, with entries); Vertrokken has none.
    expect(body.balances).toEqual([
      { personId: p1._id.toHexString(), points: 11, executions: 4, bonusPoints: 0 },
      { personId: p2._id.toHexString(), points: 10, executions: 1, bonusPoints: 0 },
      { personId: guest, points: 0, executions: 0, bonusPoints: 0 },
      { personId: former, points: 4, executions: 1, bonusPoints: 0 },
    ]);
    expect(body.balances.map((b) => b.personId)).not.toContain(gone);
  });

  it('limits the sum to the range, both days included, and reports the range', async () => {
    const res = await get('/api/points/balances?from=2026-09-14&to=2026-09-20');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json<PointsBalancesResponse>()).toEqual({
      from: '2026-09-14',
      to: '2026-09-20',
      balances: [
        { personId: p1._id.toHexString(), points: 8, executions: 3, bonusPoints: 0 },
        { personId: p2._id.toHexString(), points: 10, executions: 1, bonusPoints: 0 },
        { personId: guest, points: 0, executions: 0, bonusPoints: 0 },
      ],
    });
  });

  it('accepts a single bound', async () => {
    const onlyTo = (await get('/api/points/balances?to=2026-09-01')).json<PointsBalancesResponse>();
    expect(onlyTo.balances.find((b) => b.personId === p1._id.toHexString())).toMatchObject({ points: 3, executions: 1, bonusPoints: 0 });
    expect(onlyTo.balances.find((b) => b.personId === former)).toMatchObject({ points: 4 });
    const onlyFrom = (await get('/api/points/balances?from=2026-09-16')).json<PointsBalancesResponse>();
    expect(onlyFrom.balances.find((b) => b.personId === p1._id.toHexString())).toMatchObject({ points: 1, executions: 1, bonusPoints: 0 });
    expect(onlyFrom.balances.map((b) => b.personId)).not.toContain(former);
  });

  it('needs no profile and answers zero for everybody when the range is empty', async () => {
    const res = await get('/api/points/balances?from=2027-01-01&to=2027-01-31');
    expect(res.statusCode).toBe(200);
    expect(res.json<PointsBalancesResponse>().balances.every((b) => b.points === 0 && b.executions === 0)).toBe(true);
    expect(res.json<PointsBalancesResponse>().balances.map((b) => b.personId)).toEqual([p1._id.toHexString(), p2._id.toHexString(), guest]);
  });

  it.each([
    ['from after to', '?from=2026-09-20&to=2026-09-14', { field: 'from', message: 'from_after_to' }],
    ['a malformed day', '?from=14-09-2026', { field: 'from', message: 'invalid_day_key' }],
    ['an impossible day', '?to=2026-02-30', { field: 'to', message: 'invalid_day_key' }],
  ])('rejects %s with a field error', async (_label, query, issue) => {
    const res = await get(`/api/points/balances${query}`);
    expect(res.statusCode).toBe(400);
    expect(res.json()).toMatchObject({ code: 'validation_error', details: [issue] });
  });
});

describe('GET /api/points/entries', () => {
  const entries = (personId: string | ObjectId, from: string, to: string) =>
    get(`/api/points/entries?personId=${String(personId)}&from=${from}&to=${to}`);

  it('lists the person\'s entries in the range, newest date first and then by id, as day keys', async () => {
    const res = await entries(p1._id.toHexString(), '2026-09-01', '2026-09-20');
    expect(res.statusCode, res.body).toBe(200);
    const body = res.json<PointsEntriesResponse>();
    expect(body.entries.map((e) => [e.date, e.amount])).toEqual([
      ['2026-09-20', 1],
      ['2026-09-14', 5],
      ['2026-09-14', 2],
      ['2026-09-01', 3],
    ]);
    // Same date: the earlier id comes first.
    expect(body.entries[1]!._id < body.entries[2]!._id).toBe(true);
    expect(body.entries[0]).toEqual({
      _id: expect.stringMatching(/^[0-9a-f]{24}$/),
      key: expect.stringMatching(/^execution:[0-9a-f]{24}$/),
      kind: 'execution',
      personId: p1._id.toHexString(),
      amount: 1,
      date: '2026-09-20',
      weekStart: '2026-09-20',
      periodStart: null,
      occurrenceId: expect.stringMatching(/^[0-9a-f]{24}$/),
      taskId: null,
      titleSnapshot: 'Taak',
      source: 'live',
      createdAt: expect.stringMatching(/Z$/),
      updatedAt: expect.stringMatching(/Z$/),
    });
  });

  it('includes both bounds, leaves other people out and works for an inactive person', async () => {
    const narrow = (await entries(p1._id.toHexString(), '2026-09-14', '2026-09-14')).json<PointsEntriesResponse>();
    expect(narrow.entries.map((e) => e.amount)).toEqual([5, 2]);
    expect((await entries(p2._id.toHexString(), '2026-09-01', '2026-09-30')).json<PointsEntriesResponse>().entries).toHaveLength(1);
    const old = (await entries(former, '2026-08-01', '2026-08-31')).json<PointsEntriesResponse>();
    expect(old.entries.map((e) => e.amount)).toEqual([4]);
    expect((await entries(guest, '2026-08-01', '2026-09-30')).json<PointsEntriesResponse>().entries).toEqual([]);
  });

  it('accepts a range of exactly 371 days and rejects one day more with range_too_large', async () => {
    expect((await entries(p1._id.toHexString(), '2026-01-01', '2027-01-06')).statusCode).toBe(200);
    const tooLarge = await entries(p1._id.toHexString(), '2026-01-01', '2027-01-07');
    expect(tooLarge.statusCode).toBe(400);
    expect(tooLarge.json()).toMatchObject({ code: 'validation_error', details: [{ field: 'to', message: 'range_too_large' }] });
  });

  it.each([
    ['no person', '?from=2026-09-01&to=2026-09-20', 'personId'],
    ['an invalid person', '?personId=nope&from=2026-09-01&to=2026-09-20', 'personId'],
    ['no from', '?personId=000000000000000000000001&to=2026-09-20', 'from'],
    ['no to', '?personId=000000000000000000000001&from=2026-09-01', 'to'],
  ])('rejects %s', async (_label, query, field) => {
    const res = await get(`/api/points/entries${query}`);
    expect(res.statusCode).toBe(400);
    expect(res.json<{ code: string; details: { field: string }[] }>()).toMatchObject({ code: 'validation_error' });
    expect(res.json<{ details: { field: string }[] }>().details.map((d) => d.field)).toContain(field);
  });

  it('rejects from after to', async () => {
    const res = await entries(p1._id.toHexString(), '2026-09-20', '2026-09-14');
    expect(res.statusCode).toBe(400);
    expect(res.json()).toMatchObject({ details: [{ field: 'from', message: 'from_after_to' }] });
  });
});
