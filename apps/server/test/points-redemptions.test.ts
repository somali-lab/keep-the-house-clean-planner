import type { LightMyRequestResponse } from 'fastify';
import type { PointEntryView, PointsBalancesResponse, PointsEntriesResponse, Settings } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it } from 'vitest';
import { COLLECTIONS } from '../src/data/db.ts';
import { findPointEntries } from '../src/data/points.ts';
import { readAllCollections } from '../src/data/transfer.ts';
import { exclusively, reconcilePoints } from '../src/domain/points.ts';
import type { ExportFile } from '../src/domain/transfer.ts';
import { captureWrites, expectAudited } from './helpers/audit.ts';
import { asProfile, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';
import type { UserDoc } from '../src/data/users.ts';

/**
 * ADR-0013: a redemption is a booked ledger entry with a negative amount. The tests earn points with
 * recorded one-off work (1 point per minute), then book and undo redemptions through the HTTP API.
 * Today is Wednesday 16 September 2026.
 */
const apps: TestApp[] = [];

afterEach(async () => {
  for (const app of apps.splice(0)) await app.close();
});

const TODAY = '2026-09-16';
const TOMORROW = '2026-09-17';
const KEY_A = 'redeem-key-aaaaaaaaaaaa';
const KEY_B = 'redeem-key-bbbbbbbbbbbb';

interface Fixture {
  t: TestApp;
  /** Administrator. */
  p1: UserDoc;
  /** Member. */
  p2: UserDoc;
  call: (method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', url: string, payload?: Record<string, unknown>, actor?: UserDoc) => Promise<LightMyRequestResponse>;
  /** Records one-off work that is done on the current day (UTC date; the clock stays before 22:00 UTC), which earns `points` points for the actor. */
  earn: (points: number, actor?: UserDoc, date?: string) => Promise<void>;
  redeem: (payload: Record<string, unknown>, actor?: UserDoc) => Promise<LightMyRequestResponse>;
  balance: (person: UserDoc) => Promise<PointsBalancesResponse['balances'][number]>;
  redemptions: () => Promise<{ _id: ObjectId; amount: number; personId: ObjectId; date: Date }[]>;
}

async function fixture(): Promise<Fixture> {
  const t = await createTestApp();
  apps.push(t);
  const [p1, p2] = await seededUsers(t);
  const call: Fixture['call'] = (method, url, payload, actor = p1) =>
    t.app.inject({ method, url, headers: asProfile(actor), ...(payload ? { payload } : {}) });
  expect((await call('POST', '/api/jobs/nightly')).statusCode).toBe(200);
  const earn: Fixture['earn'] = async (points, actor = p1, date = t.clock.now().toISOString().slice(0, 10)) => {
    const res = await call(
      'POST',
      '/api/occurrences/one-off',
      { name: `Klus van ${points}`, durationMinutes: points, date, done: true },
      actor,
    );
    expect(res.statusCode, res.body).toBe(201);
  };
  const redeem: Fixture['redeem'] = (payload, actor = p1) => call('POST', '/api/points/redemptions', payload, actor);
  const balance: Fixture['balance'] = async (person) => {
    const res = await t.app.inject({ method: 'GET', url: '/api/points/balances' });
    return res.json<PointsBalancesResponse>().balances.find((b) => b.personId === person._id.toHexString())!;
  };
  const redemptions: Fixture['redemptions'] = async () =>
    (await findPointEntries(t.db, { kind: 'redemption' })).map(({ _id, amount, personId, date }) => ({ _id, amount, personId, date }));
  return { t, p1, p2, call, earn, redeem, balance, redemptions };
}

const auditOf = (t: TestApp, action: string) =>
  t.db.collection(COLLECTIONS.auditLog).find({ entity: 'points', action }).sort({ _id: 1 }).toArray();

describe('POST /api/points/redemptions', () => {
  it('books the actor\'s own redemption: a negative booked entry dated today that audits once and lowers the balance', async () => {
    const { t, p1, earn, redeem, balance, call } = await fixture();
    await earn(10);
    expect((await call('PATCH', '/api/settings', { centsPerPoint: 25 })).statusCode).toBe(200);

    const { result, entries } = await expectAudited(t, () => redeem({ points: 4, note: 'Pizza', requestId: KEY_A }), { entity: 'points', action: 'create', count: 1 });
    expect(result.statusCode, result.body).toBe(201);
    expect(result.json<PointEntryView>()).toEqual({
      _id: expect.stringMatching(/^[0-9a-f]{24}$/),
      key: expect.stringMatching(/^redemption:[0-9a-f]{24}$/),
      kind: 'redemption',
      personId: p1._id.toHexString(),
      amount: -4,
      date: TODAY,
      weekStart: '2026-09-14',
      periodStart: null,
      occurrenceId: null,
      taskId: null,
      titleSnapshot: '',
      note: 'Pizza',
      centsPerPointSnapshot: 25,
      currencyCodeSnapshot: 'EUR',
      source: 'live',
      createdAt: '2026-09-16T08:00:00.000Z',
      updatedAt: '2026-09-16T08:00:00.000Z',
    });
    // The request key stays server-side.
    expect(result.json()).not.toHaveProperty('requestId');
    expect(entries[0]).toMatchObject({ meta: { reason: 'redemption' }, after: { kind: 'redemption', amount: -4, note: 'Pizza', centsPerPointSnapshot: 25 } });
    expect((entries[0]!.actorId as ObjectId).equals(p1._id)).toBe(true);
    // The request key is retry bookkeeping: it is in the entry but never in the audit entry.
    expect(entries[0]!.after).not.toHaveProperty('requestId');
    expect(JSON.stringify(entries[0])).not.toContain(KEY_A);
    expect(await findPointEntries(t.db, { kind: 'redemption' })).toMatchObject([{ requestId: KEY_A }]);

    expect(await balance(p1)).toMatchObject({
      points: 6,
      earned: 10,
      redeemed: 4,
      executions: 1,
      money: { earned: 250, redeemed: 100, balance: 150 },
    });
  });

  it('books without a note, trims the note and treats an empty note as none', async () => {
    const { p1, earn, redeem, t } = await fixture();
    await earn(10);
    expect((await redeem({ points: 1 })).json<PointEntryView>().note).toBeNull();
    expect((await redeem({ points: 1, note: '   ' })).json<PointEntryView>().note).toBeNull();
    expect((await redeem({ points: 1, note: '  Ijsje  ' })).json<PointEntryView>().note).toBe('Ijsje');
    expect((await findPointEntries(t.db, { kind: 'redemption', personId: p1._id })).map((e) => e.note)).toEqual(expect.arrayContaining([null, null, 'Ijsje']));
  });

  it('lists the redemption among the person\'s entries, and counts executions only as executions', async () => {
    const { p1, earn, redeem, t } = await fixture();
    await earn(5);
    await redeem({ points: 2, note: 'Koffie' });
    const res = await t.app.inject({ method: 'GET', url: `/api/points/entries?personId=${p1._id.toHexString()}&from=2026-09-14&to=2026-09-20` });
    const { entries } = res.json<PointsEntriesResponse>();
    expect(entries.map((e) => [e.kind, e.amount])).toEqual(expect.arrayContaining([['execution', 5], ['redemption', -2]]));
    expect(entries.find((e) => e.kind === 'redemption')).toMatchObject({ note: 'Koffie', centsPerPointSnapshot: 0 });
    expect(entries.find((e) => e.kind === 'execution')).toMatchObject({ note: null, centsPerPointSnapshot: null });
  });

  it('lets an administrator book for anyone, and refuses a member for somebody else without writing anything', async () => {
    const { t, p1, p2, earn, redeem, balance } = await fixture();
    await earn(6, p2);
    await earn(6, p1);

    const refused = await captureWrites(t, () => redeem({ personId: p1._id.toHexString(), points: 1 }, p2));
    expect(refused.result.statusCode).toBe(403);
    expect(refused.result.json()).toMatchObject({ code: 'permission_denied' });
    expect(refused.writes).toEqual([]);
    expect(refused.auditInserts).toBe(0);

    const own = await redeem({ points: 2 }, p2);
    expect(own.statusCode, own.body).toBe(201);
    expect(own.json()).toMatchObject({ personId: p2._id.toHexString() });
    const same = await redeem({ personId: p2._id.toHexString(), points: 1 }, p2);
    expect(same.statusCode, same.body).toBe(201);

    const forOther = await redeem({ personId: p2._id.toHexString(), points: 2 }, p1);
    expect(forOther.statusCode, forOther.body).toBe(201);
    expect(forOther.json()).toMatchObject({ personId: p2._id.toHexString(), amount: -2 });
    // The audit entry names the administrator who booked it.
    const created = await auditOf(t, 'create');
    expect((created.at(-1)!.actorId as ObjectId).equals(p1._id)).toBe(true);
    expect(await balance(p2)).toMatchObject({ points: 1, redeemed: 5 });
    expect(await balance(p1)).toMatchObject({ points: 6, redeemed: 0 });
  });

  it('needs a profile and an active, known person', async () => {
    const { t, p1, earn, redeem, call } = await fixture();
    await earn(5);
    const anonymous = await t.app.inject({ method: 'POST', url: '/api/points/redemptions', payload: { points: 1 } });
    expect(anonymous.statusCode).toBe(400);
    expect(anonymous.json()).toMatchObject({ code: 'profile_required' });

    const unknown = await redeem({ personId: new ObjectId().toHexString(), points: 1 });
    expect(unknown.statusCode).toBe(400);
    expect(unknown.json()).toMatchObject({ code: 'validation_error', details: [{ field: 'personId', message: 'unknown_user' }] });

    const made = await call('POST', '/api/users', { name: 'Logé', color: '#16a34a' });
    const lodger = made.json<{ _id: string }>()._id;
    await call('PATCH', `/api/users/${lodger}`, { active: false });
    const inactive = await redeem({ personId: lodger, points: 1 });
    expect(inactive.statusCode).toBe(400);
    expect(inactive.json()).toMatchObject({ details: [{ field: 'personId', message: 'inactive_user' }] });
    expect((await findPointEntries(t.db, { kind: 'redemption', personId: p1._id })).length).toBe(0);
  });

  it('rejects a booking that would make the balance negative with insufficient_balance, and allows the exact balance', async () => {
    const { t, p1, earn, redeem, balance } = await fixture();
    await earn(5);
    const capture = await captureWrites(t, () => redeem({ points: 6 }));
    expect(capture.result.statusCode).toBe(409);
    expect(capture.result.json()).toEqual({ code: 'insufficient_balance', message: expect.any(String), balance: 5, requested: 6 });
    expect(capture.writes).toEqual([]);
    expect(capture.auditInserts).toBe(0);

    expect((await redeem({ points: 5 })).statusCode).toBe(201);
    expect(await balance(p1)).toMatchObject({ points: 0, earned: 5, redeemed: 5 });
    const again = await redeem({ points: 1 });
    expect(again.statusCode).toBe(409);
    expect(again.json()).toMatchObject({ code: 'insufficient_balance', balance: 0, requested: 1 });
  });

  it('counts every earlier redemption and bonus against the all-time balance, not the visible range', async () => {
    const { p1, earn, redeem, t } = await fixture();
    await earn(5);
    // Redeemed yesterday's worth of points earlier in the week.
    t.clock.set('2026-09-17T08:00:00.000Z');
    expect((await redeem({ points: 4 })).statusCode).toBe(201);
    t.clock.set('2026-09-23T08:00:00.000Z');
    const res = await redeem({ points: 2 });
    expect(res.statusCode).toBe(409);
    expect(res.json()).toMatchObject({ balance: 1 });
    expect((await redeem({ points: 1 })).statusCode).toBe(201);
    expect((await findPointEntries(t.db, { kind: 'redemption', personId: p1._id })).length).toBe(2);
  });

  it('never lets two simultaneous bookings overdraw the balance', async () => {
    const { p1, earn, redeem, balance } = await fixture();
    await earn(10);
    const results = await Promise.all([redeem({ points: 6 }), redeem({ points: 6 }), redeem({ points: 6 })]);
    expect(results.map((r) => r.statusCode).sort()).toEqual([201, 409, 409]);
    expect(await balance(p1)).toMatchObject({ points: 4, redeemed: 6 });
  });

  it('replays a repeated request key with 200 and writes nothing, and conflicts on a different request', async () => {
    const { t, p2, earn, redeem, balance, redemptions } = await fixture();
    await earn(10);
    const first = await redeem({ points: 3, note: 'Film', requestId: KEY_A });
    expect(first.statusCode).toBe(201);

    const replay = await captureWrites(t, () => redeem({ points: 3, note: 'Film', requestId: KEY_A }));
    expect(replay.result.statusCode, replay.result.body).toBe(200);
    expect(replay.result.json()).toEqual(first.json());
    expect(replay.writes).toEqual([]);
    expect(replay.auditInserts).toBe(0);
    expect(await redemptions()).toHaveLength(1);

    // The replay still works on another day: the key is the identity, not the date.
    t.clock.set('2026-09-17T08:00:00.000Z');
    expect((await redeem({ points: 3, note: 'Film', requestId: KEY_A })).statusCode).toBe(200);

    for (const different of [{ points: 4, note: 'Film' }, { points: 3, note: 'Andere' }, { points: 3 }]) {
      const conflict = await redeem({ ...different, requestId: KEY_A });
      expect(conflict.statusCode).toBe(409);
      expect(conflict.json()).toMatchObject({ code: 'idempotency_key_conflict' });
    }
    // Another person with the same key is a different request too.
    await earn(5, p2);
    const otherPerson = await redeem({ points: 3, note: 'Film', requestId: KEY_A }, p2);
    expect(otherPerson.statusCode).toBe(409);
    expect(otherPerson.json()).toMatchObject({ code: 'idempotency_key_conflict' });
    expect(await redemptions()).toHaveLength(1);
    expect((await balance(p2)).points).toBe(5);

    // A replay of a request that was accepted does not get stuck on the balance it would exceed now.
    expect((await redeem({ points: 7, requestId: KEY_B })).statusCode).toBe(201);
    expect((await redeem({ points: 7, requestId: KEY_B })).statusCode).toBe(200);
  });

  it('is one booking when the same request key arrives twice at once', async () => {
    const { earn, redeem, redemptions } = await fixture();
    await earn(10);
    const results = await Promise.all([redeem({ points: 3, requestId: KEY_A }), redeem({ points: 3, requestId: KEY_A })]);
    expect(results.map((r) => r.statusCode).sort()).toEqual([200, 201]);
    expect(await redemptions()).toHaveLength(1);
  });

  it.each([
    ['zero points', { points: 0 }, 'points'],
    ['negative points', { points: -2 }, 'points'],
    ['fractional points', { points: 1.5 }, 'points'],
    ['points as text', { points: '2' }, 'points'],
    ['no points', {}, 'points'],
    ['a note of 201 characters', { points: 1, note: 'x'.repeat(201) }, 'note'],
    ['an invalid request key', { points: 1, requestId: 'kort' }, 'requestId'],
    ['an invalid person id', { points: 1, personId: 'nope' }, 'personId'],
  ])('rejects %s with a field error and writes nothing', async (_label, payload, field) => {
    const { t, earn, redeem } = await fixture();
    await earn(5);
    const capture = await captureWrites(t, () => redeem(payload));
    expect(capture.result.statusCode).toBe(400);
    expect(capture.result.json<{ details: { field: string }[] }>().details.map((d) => d.field)).toContain(field);
    expect(capture.writes).toEqual([]);
  });

  it('accepts a note of exactly 200 characters', async () => {
    const { earn, redeem } = await fixture();
    await earn(5);
    const res = await redeem({ points: 1, note: 'x'.repeat(200) });
    expect(res.statusCode, res.body).toBe(201);
  });

  it('books with a factor of 0 and keeps 0 as the snapshot; balances then show no money', async () => {
    const { p1, earn, redeem, balance } = await fixture();
    await earn(5);
    const res = await redeem({ points: 1 });
    expect(res.json()).toMatchObject({ centsPerPointSnapshot: 0 });
    expect((await balance(p1)).money).toBeNull();
  });
});

describe('DELETE /api/points/redemptions/:id', () => {
  const undo = (f: Fixture, id: string, actor: UserDoc = f.p1) => f.call('DELETE', `/api/points/redemptions/${id}`, undefined, actor);

  async function booked(f: Fixture, actor: UserDoc, points = 3): Promise<string> {
    const res = await f.redeem({ points, note: 'Taart' }, actor);
    expect(res.statusCode, res.body).toBe(201);
    return res.json<PointEntryView>()._id;
  }

  it('lets the owner undo on the same day: the entry goes, one audit delete names the entry, the balance returns', async () => {
    const f = await fixture();
    await f.earn(10, f.p2);
    const id = await booked(f, f.p2);
    expect((await f.balance(f.p2)).points).toBe(7);
    const { result, entries } = await expectAudited(f.t, () => undo(f, id, f.p2), { entity: 'points', action: 'delete', count: 1 });
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json()).toEqual({ deleted: true });
    expect(entries[0]).toMatchObject({ before: { kind: 'redemption', amount: -3, note: 'Taart' }, meta: { reason: 'redemption_undone' } });
    expect(entries[0]!.before).not.toHaveProperty('requestId');
    expect((entries[0]!.actorId as ObjectId).equals(f.p2._id)).toBe(true);
    expect(await f.redemptions()).toEqual([]);
    expect(await f.balance(f.p2)).toMatchObject({ points: 10, redeemed: 0 });
  });

  it('refuses the owner on a later day with redemption_locked, and lets an administrator undo it at any time', async () => {
    const f = await fixture();
    await f.earn(10, f.p2);
    const id = await booked(f, f.p2);
    f.t.clock.set('2026-09-17T00:30:00.000Z'); // 02:30 on Thursday in Amsterdam
    const locked = await captureWrites(f.t, () => undo(f, id, f.p2));
    expect(locked.result.statusCode).toBe(403);
    expect(locked.result.json()).toMatchObject({ code: 'redemption_locked' });
    expect(locked.writes).toEqual([]);
    expect(await f.redemptions()).toHaveLength(1);

    f.t.clock.set('2027-01-05T09:00:00.000Z');
    const admin = await undo(f, id, f.p1);
    expect(admin.statusCode, admin.body).toBe(200);
    expect(await f.redemptions()).toEqual([]);
  });

  it('keeps the same day until local midnight in the household timezone', async () => {
    const f = await fixture();
    await f.earn(10, f.p2);
    const id = await booked(f, f.p2);
    f.t.clock.set('2026-09-16T21:59:00.000Z'); // 23:59 on Wednesday in Amsterdam
    expect((await undo(f, id, f.p2)).statusCode).toBe(200);
  });

  it('refuses another member and someone without a profile, and answers 404 for unknown and derived entries', async () => {
    const f = await fixture();
    await f.earn(10, f.p1);
    const id = await booked(f, f.p1);
    const other = await captureWrites(f.t, () => undo(f, id, f.p2));
    expect(other.result.statusCode).toBe(403);
    expect(other.result.json()).toMatchObject({ code: 'permission_denied' });
    expect(other.writes).toEqual([]);

    const anonymous = await f.t.app.inject({ method: 'DELETE', url: `/api/points/redemptions/${id}` });
    expect(anonymous.statusCode).toBe(400);
    expect(anonymous.json()).toMatchObject({ code: 'profile_required' });

    expect((await undo(f, new ObjectId().toHexString())).statusCode).toBe(404);
    const [execution] = await findPointEntries(f.t.db, { kind: 'execution' });
    const derived = await undo(f, execution!._id.toHexString());
    expect(derived.statusCode).toBe(404);
    expect(await findPointEntries(f.t.db, { kind: 'execution' })).toHaveLength(1);
    expect(await f.redemptions()).toHaveLength(1);
    expect((await undo(f, 'nope')).statusCode).toBe(400);
  });

  it('lets the person a redemption was booked for undo it on the same day, even when an administrator booked it', async () => {
    const f = await fixture();
    await f.earn(10, f.p2);
    const res = await f.redeem({ personId: f.p2._id.toHexString(), points: 2 }, f.p1);
    expect(res.statusCode).toBe(201);
    expect((await undo(f, res.json<PointEntryView>()._id, f.p2)).statusCode).toBe(200);
  });

  it('a second undo of the same entry is a 404 and writes nothing', async () => {
    const f = await fixture();
    await f.earn(10);
    const id = await booked(f, f.p1);
    expect((await undo(f, id)).statusCode).toBe(200);
    const capture = await captureWrites(f.t, () => undo(f, id));
    expect(capture.result.statusCode).toBe(404);
    expect(capture.writes).toEqual([]);
  });
});

describe('redemptions and the reconciliation', () => {
  it('keeps redemptions when the ledger is recomputed, also when the earned points are gone', async () => {
    const f = await fixture();
    await f.earn(10);
    const redeemed = await f.redeem({ points: 4, note: 'Pizza' });
    expect(redeemed.statusCode).toBe(201);
    const before = await f.redemptions();

    const result = await reconcilePoints(f.t.systemCtx(), 'nightly');
    expect(result).toMatchObject({ created: 0, updated: 0, removed: 0 });
    expect((await f.call('POST', '/api/points/recompute')).statusCode).toBe(200);
    expect(await f.redemptions()).toEqual(before);

    // Drift repair removes an execution entry without an occurrence, never the redemption.
    // eslint-disable-next-line no-restricted-syntax -- creates drift that only a reconciliation repairs
    await f.t.db.collection(COLLECTIONS.pointEntries).insertOne({
      _id: new ObjectId(),
      key: `execution:${new ObjectId().toHexString()}`,
      kind: 'execution',
      personId: f.p1._id,
      amount: 2,
      date: new Date('2026-09-16T00:00:00.000Z'),
      weekStart: new Date('2026-09-14T00:00:00.000Z'),
      occurrenceId: null,
      taskId: null,
      titleSnapshot: 'Verdwaald',
      source: 'live',
      createdAt: new Date(),
      updatedAt: new Date(),
    });
    expect(await reconcilePoints(f.t.systemCtx(), 'admin')).toMatchObject({ removed: 1 });
    expect(await f.redemptions()).toEqual(before);

    // Undoing the work leaves the redemption standing: the balance is negative, nothing is rewritten.
    const [occurrence] = (await f.t.db.collection(COLLECTIONS.occurrences).find({ recordedDone: true }).toArray()) as unknown as { _id: ObjectId }[];
    expect((await f.call('POST', `/api/occurrences/${occurrence!._id.toHexString()}/retract`)).statusCode).toBe(200);
    expect(await reconcilePoints(f.t.systemCtx(), 'nightly')).toMatchObject({ created: 0, updated: 0, removed: 0 });
    expect(await f.redemptions()).toEqual(before);
    expect(await f.balance(f.p1)).toMatchObject({ points: -4, earned: 0, redeemed: 4 });
  });

  it('a recompute with a redemption present writes nothing and audits nothing', async () => {
    const f = await fixture();
    await f.earn(10);
    await f.redeem({ points: 4 });
    const capture = await captureWrites(f.t, () => reconcilePoints(f.t.systemCtx(), 'nightly'));
    expect(capture.writes).toEqual([]);
    expect(capture.auditInserts).toBe(0);
  });
});

describe('GET /api/points/redemptions/count', () => {
  it('counts the redemptions without a profile', async () => {
    const f = await fixture();
    await f.earn(10);
    const count = async () => (await f.t.app.inject({ method: 'GET', url: '/api/points/redemptions/count' })).json();
    expect(await count()).toEqual({ count: 0 });
    await f.redeem({ points: 2 });
    await f.redeem({ points: 3 });
    expect(await count()).toEqual({ count: 2 });
  });
});

describe('the ledger queue', () => {
  it('makes a live execution sync wait for a running reconciliation or booking, and runs on afterwards', async () => {
    const f = await fixture();
    let release!: () => void;
    const held = exclusively(f.t.db, () => new Promise<void>((resolve) => { release = resolve; }));
    // A recorded check-off syncs its entry; while the queue is held that write cannot happen.
    const earning = f.earn(5);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(await findPointEntries(f.t.db, { kind: 'execution' })).toEqual([]);
    release();
    await held;
    await earning;
    expect(await findPointEntries(f.t.db, { kind: 'execution' })).toHaveLength(1);
  });

  it('does not deadlock when check-offs, bookings and reconciliations run at once, and ends consistent', async () => {
    const f = await fixture();
    await f.earn(10);
    const calls = [
      f.earn(3),
      f.redeem({ points: 2 }),
      f.call('POST', '/api/points/recompute'),
      f.earn(4, f.p2),
      f.redeem({ points: 1 }),
      reconcilePoints(f.t.systemCtx(), 'nightly'),
    ];
    await Promise.all(calls);
    expect((await findPointEntries(f.t.db, { kind: 'execution' })).map((e) => e.amount).sort()).toEqual([10, 3, 4]);
    expect(await reconcilePoints(f.t.systemCtx(), 'nightly')).toMatchObject({ created: 0, updated: 0, removed: 0 });
    expect(await f.balance(f.p1)).toMatchObject({ points: 10, redeemed: 3 });
  });
});

describe('the statistics reset', () => {
  async function twoDays(f: Fixture) {
    await f.earn(10);
    expect((await f.redeem({ points: 4, note: 'Dinsdag' })).statusCode).toBe(201);
    f.t.clock.set('2026-09-17T08:00:00.000Z');
    await f.earn(10, f.p1, TOMORROW);
    expect((await f.redeem({ points: 3, note: 'Donderdag' })).statusCode).toBe(201);
    expect(await f.redemptions()).toHaveLength(2);
  }
  const resetAudit = (t: TestApp) => t.db.collection(COLLECTIONS.auditLog).find({ entity: 'settings', action: 'reset' }).toArray();

  it('purging before a day removes the redemptions dated before it and keeps the later ones, counted in the one reset entry', async () => {
    const f = await fixture();
    await twoDays(f);
    const res = await f.call('DELETE', `/api/stats?before=${TOMORROW}`);
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toMatchObject({ removedRedemptions: 1, removedPointEntries: 2 });
    const left = await findPointEntries(f.t.db, { kind: 'redemption' });
    expect(left.map((e) => [e.amount, e.note])).toEqual([[-3, 'Donderdag']]);
    const audit = await resetAudit(f.t);
    expect(audit).toHaveLength(1);
    expect(audit[0]!.meta).toMatchObject({ scoped: true, removedPointEntries: 2, removedRedemptions: 1 });
    expect(await f.t.db.collection(COLLECTIONS.auditLog).countDocuments({ entity: 'points', action: 'delete' })).toBe(0);
  });

  it('starting over removes every redemption', async () => {
    const f = await fixture();
    await twoDays(f);
    const res = await f.call('DELETE', '/api/stats');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toMatchObject({ removedRedemptions: 2, removedPointEntries: 4 });
    expect(await findPointEntries(f.t.db)).toEqual([]);
    expect((await resetAudit(f.t))[0]!.meta).toMatchObject({ scoped: false, removedRedemptions: 2 });
  });
});

describe('transfer', () => {
  async function exported(f: Fixture): Promise<ExportFile> {
    return (await f.t.app.inject({ method: 'GET', url: '/api/export/json' })).json<ExportFile>();
  }
  const importFile = (f: Fixture, file: unknown) => f.call('POST', '/api/import/json?mode=replace&confirm=true', file as Record<string, unknown>);

  it('exports the redemptions only, as schema version 5, and keeps them on a round trip while the rest is rebuilt', async () => {
    const f = await fixture();
    await f.earn(10);
    await f.earn(10, f.p2);
    expect((await f.call('PATCH', '/api/settings', { currencyCode: 'USD', centsPerPoint: 15 })).statusCode).toBe(200);
    expect((await f.redeem({ points: 4, note: 'Pizza', requestId: KEY_A })).statusCode).toBe(201);
    const file = await exported(f);
    expect(file.schemaVersion).toBe(5);
    expect(file.collections.pointEntries).toHaveLength(1);
    expect(file.collections.pointEntries[0]).toMatchObject({ kind: 'redemption', amount: -4, note: 'Pizza', centsPerPointSnapshot: 15, currencyCodeSnapshot: 'USD', requestId: KEY_A });
    expect(file.collections.settings[0]).toMatchObject({ currencyCode: 'USD', centsPerPoint: 15 });

    const target = await fixture();
    expect((await importFile(target, file)).statusCode).toBe(200);
    // The import replaces the users, so act as the imported administrator.
    expect((await readAllCollections(target.t.db)).pointEntries).toHaveLength(1);
    const redemption = (await findPointEntries(target.t.db, { kind: 'redemption' }))[0]!;
    expect(redemption).toMatchObject({ amount: -4, note: 'Pizza', centsPerPointSnapshot: 15, currencyCodeSnapshot: 'USD', requestId: KEY_A, source: 'live' });
    // Derived entries were rebuilt from the occurrences: both executions are back, the balance is the same.
    expect((await findPointEntries(target.t.db, { kind: 'execution' })).map((e) => e.amount).sort()).toEqual([10, 10]);
    const before = (await f.t.app.inject({ method: 'GET', url: '/api/points/balances' })).json<PointsBalancesResponse>();
    const after = (await target.t.app.inject({ method: 'GET', url: '/api/points/balances' })).json<PointsBalancesResponse>();
    expect(after).toEqual(before);
    // A replay of the original request key still finds its booking after the import.
    const replay = await target.redeem({ points: 4, note: 'Pizza', requestId: KEY_A }, (await seededUsers(target.t))[0]);
    expect(replay.statusCode, replay.body).toBe(200);
  });

  it('writes the same file again after an import: the export is stable', async () => {
    const f = await fixture();
    await f.earn(10);
    await f.redeem({ points: 4, note: 'Pizza' });
    const file = await exported(f);
    expect((await importFile(f, file)).statusCode).toBe(200);
    const again = await exported(f);
    expect(again.collections.pointEntries).toEqual(file.collections.pointEntries);
  });

  it('imports a version 4 file, which has no redemptions, and drops the redemptions it replaces', async () => {
    const f = await fixture();
    await f.earn(10);
    const legacy = structuredClone(await exported(f));
    legacy.schemaVersion = 4 as never;
    delete (legacy.collections as Partial<ExportFile['collections']>).pointEntries;
    delete legacy.collections.settings[0]!.currencyCode;
    delete legacy.collections.settings[0]!.centsPerPoint;

    const target = await fixture();
    await target.earn(10);
    await target.redeem({ points: 2 });
    expect(await target.redemptions()).toHaveLength(1);
    // The redemptions that would be lost must be acknowledged; nothing is written until they are.
    const refused = await captureWrites(target.t, () => importFile(target, legacy));
    expect(refused.result.statusCode).toBe(409);
    expect(refused.result.json()).toMatchObject({ code: 'redemptions_would_be_removed', count: 1 });
    expect(refused.writes).toEqual([]);
    expect(await target.redemptions()).toHaveLength(1);

    const res = await target.call('POST', '/api/import/json?mode=replace&confirm=true&acknowledgeRedemptions=true', legacy as unknown as Record<string, unknown>);
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toMatchObject({ replaced: { pointEntries: 0 }, removedRedemptions: 1 });
    // The import audit entry says how many redemptions it removed.
    const imports = await target.t.db.collection(COLLECTIONS.auditLog).find({ entity: 'import' }).toArray();
    expect(imports.at(-1)!.after).toMatchObject({ removedRedemptions: 1 });
    expect(await target.redemptions()).toEqual([]);
    expect((await findPointEntries(target.t.db, { kind: 'execution' })).map((e) => e.amount)).toEqual([10]);
    expect((await target.t.app.inject({ method: 'GET', url: '/api/settings' })).json<Settings>()).toMatchObject({ currencyCode: 'EUR', centsPerPoint: 0 });
  });

  it('needs no acknowledgement for an older file when there are no redemptions, nor for a version 5 file', async () => {
    const f = await fixture();
    await f.earn(10);
    const old = structuredClone(await exported(f));
    old.schemaVersion = 3 as never;
    delete (old.collections as Partial<ExportFile['collections']>).pointEntries;
    const none = await importFile(f, old);
    expect(none.statusCode, none.body).toBe(200);
    expect(none.json()).toMatchObject({ removedRedemptions: 0 });

    await f.redeem({ points: 2 });
    const current = await exported(f);
    const res = await importFile(f, current);
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toMatchObject({ removedRedemptions: 1, replaced: { pointEntries: 1 } });
    expect(await f.redemptions()).toHaveLength(1);
  });

  it('imports a version 5 file from before the currency was kept with a booking', async () => {
    const f = await fixture();
    await f.earn(10);
    await f.redeem({ points: 2 });
    const file = structuredClone(await exported(f));
    delete file.collections.pointEntries[0]!.currencyCodeSnapshot;
    expect((await importFile(f, file)).statusCode).toBe(200);
    const entries = (await f.t.app.inject({ method: 'GET', url: `/api/points/entries?personId=${f.p1._id.toHexString()}&from=2026-09-14&to=2026-09-20` })).json<PointsEntriesResponse>().entries;
    expect(entries.find((e) => e.kind === 'redemption')).toMatchObject({ currencyCodeSnapshot: null });
  });

  it.each([
    ['a version 5 file without its redemptions', (file: ExportFile) => { delete (file.collections as Partial<ExportFile['collections']>).pointEntries; }, 'collections.pointEntries', 'required'],
    [
      'a redemption of a person who is not in the file',
      (file: ExportFile) => { file.collections.pointEntries[0]!.personId = { $oid: new ObjectId().toHexString() }; },
      'collections.pointEntries.0.personId',
      'unknown_user',
    ],
    [
      'a redemption that is not a redemption',
      (file: ExportFile) => { file.collections.pointEntries[0]!.kind = 'execution'; },
      'collections.pointEntries.0.kind',
      expect.any(String),
    ],
    [
      'a redemption with a positive amount',
      (file: ExportFile) => { file.collections.pointEntries[0]!.amount = 4; },
      'collections.pointEntries.0.amount',
      expect.any(String),
    ],
    [
      'a duplicate redemption key',
      (file: ExportFile) => { file.collections.pointEntries.push({ ...file.collections.pointEntries[0]!, _id: { $oid: new ObjectId().toHexString() }, requestId: null }); },
      'collections.pointEntries.1.key',
      'duplicate_key',
    ],
    [
      'a duplicate redemption request key',
      (file: ExportFile) => {
        const id = new ObjectId().toHexString();
        file.collections.pointEntries.push({ ...file.collections.pointEntries[0]!, _id: { $oid: id }, key: `redemption:${id}` });
      },
      'collections.pointEntries.1.requestId',
      'duplicate_request_id',
    ],
    ['an unknown currency', (file: ExportFile) => { file.collections.settings[0]!.currencyCode = 'ZZZ'; }, 'collections.settings.0.currencyCode', 'invalid_currency_code'],
  ])('refuses %s before writing anything', async (_label, mutate, field, message) => {
    const f = await fixture();
    await f.earn(10);
    await f.redeem({ points: 4, requestId: KEY_A });
    const broken = structuredClone(await exported(f));
    mutate(broken);
    const capture = await captureWrites(f.t, () => importFile(f, broken));
    expect(capture.result.statusCode).toBe(400);
    expect(capture.result.json<{ details: { field: string; message: string }[] }>().details).toContainEqual({ field, message });
    expect(capture.writes).toEqual([]);
  });
});

describe('the conversion settings', () => {
  const settings = (f: Fixture) => f.t.app.inject({ method: 'GET', url: '/api/settings' }).then((r) => r.json<Settings>());
  const auditSettings = (t: TestApp) => t.db.collection(COLLECTIONS.auditLog).find({ entity: 'settings', action: 'update' }).sort({ _id: 1 }).toArray();

  it('defaults to EUR and no money', async () => {
    const f = await fixture();
    expect(await settings(f)).toMatchObject({ currencyCode: 'EUR', centsPerPoint: 0 });
    const balances = (await f.t.app.inject({ method: 'GET', url: '/api/points/balances' })).json<PointsBalancesResponse>();
    expect(balances).toMatchObject({ currencyCode: 'EUR', centsPerPoint: 0 });
    expect(balances.balances.every((b) => b.money === null)).toBe(true);
  });

  it('lets an administrator set the currency and the factor, audited once with before and after', async () => {
    const f = await fixture();
    const { result, entries } = await expectAudited(f.t, () => f.call('PATCH', '/api/settings', { currencyCode: 'USD', centsPerPoint: 10 }), {
      entity: 'settings',
      action: 'update',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json()).toMatchObject({ currencyCode: 'USD', centsPerPoint: 10 });
    expect(entries[0]).toMatchObject({ before: {}, after: { currencyCode: 'USD', centsPerPoint: 10 } });
    expect(await settings(f)).toMatchObject({ currencyCode: 'USD', centsPerPoint: 10 });

    const second = await expectAudited(f.t, () => f.call('PATCH', '/api/settings', { centsPerPoint: 0 }), { entity: 'settings', action: 'update', count: 1 });
    expect(second.entries[0]).toMatchObject({ before: { centsPerPoint: 10 }, after: { centsPerPoint: 0 } });
  });

  it('a patch that changes nothing writes and audits nothing, also when it states the default of an unset value', async () => {
    const f = await fixture();
    for (const body of [{ currencyCode: 'EUR', centsPerPoint: 0 }, { centsPerPoint: 0 }]) {
      const capture = await captureWrites(f.t, () => f.call('PATCH', '/api/settings', body));
      expect(capture.result.statusCode, capture.result.body).toBe(200);
      expect(capture.writes).toEqual([]);
      expect(capture.auditInserts).toBe(0);
    }
    await f.call('PATCH', '/api/settings', { currencyCode: 'GBP', centsPerPoint: 5 });
    const before = (await auditSettings(f.t)).length;
    const capture = await captureWrites(f.t, () => f.call('PATCH', '/api/settings', { currencyCode: 'GBP', centsPerPoint: 5 }));
    expect(capture.writes).toEqual([]);
    expect((await auditSettings(f.t)).length).toBe(before);
  });

  it('is for administrators only', async () => {
    const f = await fixture();
    const capture = await captureWrites(f.t, () => f.call('PATCH', '/api/settings', { centsPerPoint: 10 }, f.p2));
    expect(capture.result.statusCode).toBe(403);
    expect(capture.writes).toEqual([]);
  });

  it.each([
    ['a lowercase code', { currencyCode: 'eur' }, 'currencyCode'],
    ['a code of four letters', { currencyCode: 'EURO' }, 'currencyCode'],
    ['a code that is no currency', { currencyCode: 'ZZZ' }, 'currencyCode'],
    ['a currency without fraction digits', { currencyCode: 'JPY' }, 'currencyCode'],
    ['a currency with three fraction digits', { currencyCode: 'KWD' }, 'currencyCode'],
    ['a negative factor', { centsPerPoint: -1 }, 'centsPerPoint'],
    ['a factor above 10000', { centsPerPoint: 10001 }, 'centsPerPoint'],
    ['a fractional factor', { centsPerPoint: 2.5 }, 'centsPerPoint'],
    ['a factor as text', { centsPerPoint: '10' }, 'centsPerPoint'],
  ])('rejects %s', async (_label, body, field) => {
    const f = await fixture();
    const capture = await captureWrites(f.t, () => f.call('PATCH', '/api/settings', body));
    expect(capture.result.statusCode).toBe(400);
    expect(capture.result.json<{ details: { field: string }[] }>().details.map((d) => d.field)).toContain(field);
    expect(capture.writes).toEqual([]);
  });

  it('names the reason when a currency does not have two fraction digits, and accepts real two-digit currencies', async () => {
    const f = await fixture();
    for (const code of ['JPY', 'KWD']) {
      const res = await f.call('PATCH', '/api/settings', { currencyCode: code });
      expect(res.statusCode).toBe(400);
      expect(res.json()).toMatchObject({ details: [{ field: 'currencyCode', message: 'currency_not_two_decimals' }] });
    }
    const unknown = await f.call('PATCH', '/api/settings', { currencyCode: 'ZZZ' });
    expect(unknown.json()).toMatchObject({ details: [{ field: 'currencyCode', message: 'invalid_currency_code' }] });
    for (const code of ['EUR', 'USD', 'CHF']) expect((await f.call('PATCH', '/api/settings', { currencyCode: code })).statusCode).toBe(200);
    expect((await settings(f)).currencyCode).toBe('CHF');
  });

  it('accepts the bounds 0 and 10000', async () => {
    const f = await fixture();
    expect((await f.call('PATCH', '/api/settings', { centsPerPoint: 10000 })).statusCode).toBe(200);
    expect((await f.call('PATCH', '/api/settings', { centsPerPoint: 0 })).statusCode).toBe(200);
  });

  it('keeps the currency and the factor of a booking after the household switches currency', async () => {
    const f = await fixture();
    await f.earn(10);
    await f.call('PATCH', '/api/settings', { currencyCode: 'EUR', centsPerPoint: 20 });
    const first = await f.redeem({ points: 2 });
    await f.call('PATCH', '/api/settings', { currencyCode: 'USD', centsPerPoint: 30 });
    const second = await f.redeem({ points: 3 });
    expect(first.json()).toMatchObject({ currencyCodeSnapshot: 'EUR', centsPerPointSnapshot: 20 });
    expect(second.json()).toMatchObject({ currencyCodeSnapshot: 'USD', centsPerPointSnapshot: 30 });
    const entries = (await f.t.app.inject({ method: 'GET', url: `/api/points/entries?personId=${f.p1._id.toHexString()}&from=2026-09-14&to=2026-09-20` })).json<PointsEntriesResponse>().entries;
    expect(entries.filter((e) => e.kind === 'redemption').map((e) => [e.amount, e.currencyCodeSnapshot, e.centsPerPointSnapshot]).sort()).toEqual([
      [-2, 'EUR', 20],
      [-3, 'USD', 30],
    ]);
    // Derived entries carry no currency; the booking audit entry does.
    expect(entries.find((e) => e.kind === 'execution')).toMatchObject({ currencyCodeSnapshot: null });
    const created = await auditOf(f.t, 'create');
    expect(created.at(-1)!.after).toMatchObject({ currencyCodeSnapshot: 'USD', centsPerPointSnapshot: 30 });
    // Balances are in the currency in force now.
    const balances = (await f.t.app.inject({ method: 'GET', url: '/api/points/balances' })).json<PointsBalancesResponse>();
    expect(balances.currencyCode).toBe('USD');
  });

  it('keeps the factor a redemption was booked at, while the balances use the factor in force now', async () => {
    const f = await fixture();
    await f.earn(10);
    await f.call('PATCH', '/api/settings', { centsPerPoint: 20 });
    const res = await f.redeem({ points: 5 });
    expect(res.json()).toMatchObject({ centsPerPointSnapshot: 20 });
    await f.call('PATCH', '/api/settings', { centsPerPoint: 30 });
    expect((await findPointEntries(f.t.db, { kind: 'redemption' }))[0]).toMatchObject({ centsPerPointSnapshot: 20 });
    expect(await f.balance(f.p1)).toMatchObject({ money: { earned: 300, redeemed: 150, balance: 150 } });
  });
});
