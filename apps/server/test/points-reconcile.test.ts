/* eslint-disable no-restricted-syntax -- these tests write old and drifted data straight to the database, which no repository does, to prove the reconciliation repairs it */
import type { LightMyRequestResponse } from 'fastify';
import { fromDayKey, type OccurrenceView, type PointsRecomputeResult } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { findOccurrenceById, type OccurrenceDoc } from '../src/data/occurrences.ts';
import { executionKey, findPointEntries, findPointEntryByKey } from '../src/data/points.ts';
import { readAllCollections } from '../src/data/transfer.ts';
import { reconcilePoints } from '../src/domain/points.ts';
import { importData, parseImport, type ExportFile } from '../src/domain/transfer.ts';
import { captureWrites } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type CreateTestAppOptions, type TestApp } from './helpers/testApp.ts';
import type { UserDoc } from '../src/data/users.ts';

/**
 * ADR-0011: the reconciliation makes the ledger match the occurrences, idempotently. Monday 14 Sep 2026
 * is the first day of cycle 0; the plan has one task of 30 minutes (3 points by default) on Monday
 * (person 1), Tuesday (person 2) and Wednesday (nobody).
 */
const apps: TestApp[] = [];

afterEach(async () => {
  for (const app of apps.splice(0)) await app.close();
});

interface Fixture {
  t: TestApp;
  p1: UserDoc;
  p2: UserDoc;
  task: string;
  occurrence: (date: string) => Promise<string>;
  call: (method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', url: string, payload?: Record<string, unknown>, actor?: UserDoc) => Promise<LightMyRequestResponse>;
}

async function fixture(options: CreateTestAppOptions = {}): Promise<Fixture> {
  const t = await createTestApp({ now: '2026-09-14T06:00:00.000Z', ...options });
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
  const occurrence = async (date: string) => {
    const res = await t.app.inject({ method: 'GET', url: `/api/occurrences?from=${date}&to=${date}` });
    return res.json<OccurrenceView[]>().find((o) => o.taskId === task)!._id;
  };
  return { t, p1, p2, task, occurrence, call };
}

const reconcileAudit = (t: TestApp) =>
  t.db.collection(COLLECTIONS.auditLog).find({ entity: 'points', action: 'recompute' }).sort({ _id: 1 }).toArray();

/** Turns an occurrence into old data: done without a snapshot and without a ledger entry. */
async function makeLegacyDone(t: TestApp, id: string, fields: Partial<OccurrenceDoc>): Promise<void> {
  await t.db.collection<OccurrenceDoc>(COLLECTIONS.occurrences).updateOne(
    { _id: new ObjectId(id) },
    { $set: { status: 'done', completedAt: new Date('2026-09-14T10:00:00.000Z'), ...fields }, $unset: { pointsSnapshot: '' } },
  );
  await t.db.collection(COLLECTIONS.pointEntries).deleteMany({ occurrenceId: new ObjectId(id) });
}

/** An extra occurrence that exists next to the generated ones, inserted directly like an old import would. */
async function insertLegacyAdhoc(t: TestApp, template: string, fields: Partial<OccurrenceDoc>): Promise<ObjectId> {
  const base = (await findOccurrenceById(t.db, new ObjectId(template)))!;
  const { pointsSnapshot: _p, ...rest } = base;
  const doc: OccurrenceDoc = {
    ...rest,
    _id: new ObjectId(),
    origin: 'adhoc',
    planId: null,
    status: 'done',
    completedAt: new Date('2026-09-14T10:00:00.000Z'),
    completedBy: null,
    ...fields,
  };
  await t.db.collection<OccurrenceDoc>(COLLECTIONS.occurrences).insertOne(doc);
  return doc._id;
}

describe('reconcilePoints: retroactive points', () => {
  it('awards historical executions per the ADR, writes one summary, and a second run writes and audits nothing', async () => {
    const { t, p1, p2, task, occurrence } = await fixture();
    const monday = await occurrence('2026-09-14'); // assigned to p1
    const tuesday = await occurrence('2026-09-15'); // assigned to p2
    const wednesday = await occurrence('2026-09-16'); // assigned to nobody
    // Legacy done with completedBy: credited to that person, not the assignee.
    await makeLegacyDone(t, monday, { completedBy: p2._id });
    // Legacy done without completedBy: credited to the assignee.
    await makeLegacyDone(t, tuesday, { completedBy: null });
    // Legacy done without anybody: counted as unattributed, no entry.
    await makeLegacyDone(t, wednesday, { completedBy: null });
    // The task of this occurrence no longer exists, and a one-off task has none: the duration rule applies.
    const orphanTask = await insertLegacyAdhoc(t, monday, { taskId: new ObjectId(), durationMinutesSnapshot: 25, completedBy: p1._id });
    const oneOff = await insertLegacyAdhoc(t, monday, { taskId: null, durationMinutesSnapshot: 95, assigneeId: p2._id, taskNameSnapshot: 'Zolder vegen' });
    // A task from before points existed.
    await t.db.collection(COLLECTIONS.tasks).updateOne({ _id: new ObjectId(task) }, { $unset: { points: '' } });

    const result = await reconcilePoints(t.systemCtx(), 'startup');
    expect(result).toMatchObject({
      trigger: 'startup',
      tasksDefaulted: 1,
      snapshotsSet: 5,
      created: 4,
      updated: 0,
      removed: 0,
      unattributed: 1,
      corrections: [],
    });

    const entry = async (id: string | ObjectId) => findPointEntryByKey(t.db, executionKey(new ObjectId(id)));
    expect(await entry(monday)).toMatchObject({ personId: p2._id, amount: 3, source: 'backfill', titleSnapshot: 'Stofzuigen', date: fromDayKey('2026-09-14') });
    expect(await entry(tuesday)).toMatchObject({ personId: p2._id, amount: 3 });
    expect(await entry(wednesday)).toBeNull();
    expect(await entry(orphanTask)).toMatchObject({ personId: p1._id, amount: 3, taskId: expect.any(ObjectId) });
    expect(await entry(oneOff)).toMatchObject({ personId: p2._id, amount: 10, taskId: null, titleSnapshot: 'Zolder vegen' });
    expect((await findOccurrenceById(t.db, new ObjectId(wednesday)))!.pointsSnapshot).toBe(3);
    expect((await t.db.collection(COLLECTIONS.tasks).findOne({ _id: new ObjectId(task) }))!.points).toBe(3);

    const audit = await reconcileAudit(t);
    expect(audit).toHaveLength(1);
    expect(audit[0]).toMatchObject({
      entity: 'points',
      action: 'recompute',
      source: 'system',
      meta: { trigger: 'startup', tasksDefaulted: 1, snapshotsSet: 5, created: 4, updated: 0, removed: 0, unattributed: 1, corrections: [] },
    });
    // The per-entry audit entries of a backfill are not written; the summary stands for all of them.
    expect(await t.db.collection(COLLECTIONS.auditLog).countDocuments({ entity: 'points', action: 'create' })).toBe(0);

    const again = await captureWrites(t, () => reconcilePoints(t.systemCtx(), 'nightly'));
    expect(again.writes).toEqual([]);
    expect(again.auditInserts).toBe(0);
    expect(again.result).toMatchObject({ tasksDefaulted: 0, snapshotsSet: 0, created: 0, updated: 0, removed: 0 });
    expect(await reconcileAudit(t)).toHaveLength(1);
    expect(await findPointEntries(t.db)).toHaveLength(4);
  });

  it('never rewrites a snapshot from a task value that changed afterwards', async () => {
    const { t, p1, task, occurrence, call } = await fixture();
    const monday = await occurrence('2026-09-14');
    expect((await call('PATCH', `/api/occurrences/${monday}`, { action: 'complete' })).statusCode).toBe(200);
    expect((await call('PATCH', `/api/tasks/${task}`, { points: 9 })).statusCode).toBe(200);
    const result = await reconcilePoints(t.systemCtx(), 'admin');
    expect(result).toMatchObject({ created: 0, updated: 0, removed: 0 });
    expect(await findPointEntryByKey(t.db, executionKey(new ObjectId(monday)))).toMatchObject({ personId: p1._id, amount: 3 });
  });

  it('heals drift between the ledger and the occurrences and lists every correction', async () => {
    const { t, p1, p2, occurrence, call } = await fixture();
    const monday = await occurrence('2026-09-14');
    const tuesday = await occurrence('2026-09-15');
    const wednesday = await occurrence('2026-09-16');
    expect((await call('PATCH', `/api/occurrences/${monday}`, { action: 'complete' })).statusCode).toBe(200);
    expect((await call('PATCH', `/api/occurrences/${tuesday}`, { action: 'complete', completedBy: p2._id.toHexString() })).statusCode).toBe(200);
    expect((await call('PATCH', `/api/occurrences/${wednesday}`, { action: 'complete' })).statusCode).toBe(200);
    const col = t.db.collection(COLLECTIONS.pointEntries);
    const mondayKey = executionKey(new ObjectId(monday));
    const stray = executionKey(new ObjectId());
    // Drift: a wrong person and amount, a lost entry and an entry without an occurrence.
    await col.updateOne({ key: mondayKey }, { $set: { personId: p2._id, amount: 9 } });
    await col.deleteOne({ key: executionKey(new ObjectId(tuesday)) });
    const orphan = (await col.findOne({ key: executionKey(new ObjectId(wednesday)) }))!;
    const { _id: _unused, ...orphanFields } = orphan;
    await col.insertOne({ ...orphanFields, _id: new ObjectId(), key: stray, occurrenceId: new ObjectId(), personId: p1._id, amount: 4 });

    const result = await reconcilePoints(t.systemCtx(), 'nightly');
    expect(result).toMatchObject({ trigger: 'nightly', created: 1, updated: 1, removed: 1, snapshotsSet: 0, tasksDefaulted: 0 });
    expect(result.corrections).toEqual(
      expect.arrayContaining([
        { key: mondayKey, from: { personId: p2._id.toHexString(), amount: 9 }, to: { personId: p1._id.toHexString(), amount: 3 } },
        { key: stray, from: { personId: p1._id.toHexString(), amount: 4 }, to: null },
      ]),
    );
    expect(result.corrections).toHaveLength(2);

    const entries = await findPointEntries(t.db);
    expect(entries.map((e) => [e.key, e.personId.toHexString(), e.amount]).sort()).toEqual(
      [
        [mondayKey, p1._id.toHexString(), 3],
        [executionKey(new ObjectId(tuesday)), p2._id.toHexString(), 3],
        [executionKey(new ObjectId(wednesday)), p1._id.toHexString(), 3],
      ].sort(),
    );
    expect(entries.find((e) => e.key === mondayKey)!.source).toBe('recompute');
    const audit = await reconcileAudit(t);
    expect(audit).toHaveLength(1);
    expect(audit[0]!.meta).toMatchObject({ trigger: 'nightly', created: 1, updated: 1, removed: 1, corrections: result.corrections });

    const again = await captureWrites(t, () => reconcilePoints(t.systemCtx(), 'nightly'));
    expect(again.writes).toEqual([]);
    expect(again.auditInserts).toBe(0);
  });

  it('does nothing without settings', async () => {
    const { t } = await fixture();
    await t.db.collection(COLLECTIONS.settings).deleteMany({});
    const capture = await captureWrites(t, () => reconcilePoints(t.systemCtx(), 'startup'));
    expect(capture.writes).toEqual([]);
    expect(capture.auditInserts).toBe(0);
  });
});

describe('POST /api/points/recompute', () => {
  it('lets an administrator reconcile, answering the counts, and audits it once', async () => {
    const { t, p1, occurrence, call } = await fixture();
    const monday = await occurrence('2026-09-14');
    expect((await call('PATCH', `/api/occurrences/${monday}`, { action: 'complete' })).statusCode).toBe(200);
    await t.db.collection(COLLECTIONS.pointEntries).deleteMany({});

    const res = await call('POST', '/api/points/recompute');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json<PointsRecomputeResult>()).toMatchObject({ trigger: 'admin', created: 1, updated: 0, removed: 0, corrections: [] });
    const audit = await reconcileAudit(t);
    expect(audit).toHaveLength(1);
    expect(audit[0]).toMatchObject({ source: 'ui', meta: { trigger: 'admin', created: 1 } });
    expect((audit[0]!.actorId as ObjectId).equals(p1._id)).toBe(true);

    const noop = await captureWrites(t, () => call('POST', '/api/points/recompute'));
    expect(noop.result.statusCode).toBe(200);
    expect(noop.writes).toEqual([]);
    expect(noop.auditInserts).toBe(0);
  });

  it('is refused for a household member and without a profile, and writes nothing', async () => {
    const { t, p2 } = await fixture();
    const capture = await captureWrites(t, async () => ({
      member: await t.app.inject({ method: 'POST', url: '/api/points/recompute', headers: asProfile(p2) }),
      anonymous: await t.app.inject({ method: 'POST', url: '/api/points/recompute' }),
    }));
    expect(capture.result.member.statusCode).toBe(403);
    expect(capture.result.member.json()).toMatchObject({ code: 'permission_denied' });
    expect(capture.result.anonymous.statusCode).toBe(400);
    expect(capture.writes).toEqual([]);
    expect(capture.auditInserts).toBe(0);
  });

  it('is run by the nightly job as well', async () => {
    const { t, occurrence } = await fixture();
    const monday = await occurrence('2026-09-14');
    await makeLegacyDone(t, monday, { completedBy: null });
    expect((await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile((await seededUsers(t))[0]) })).statusCode).toBe(200);
    expect(await findPointEntryByKey(t.db, executionKey(new ObjectId(monday)))).toMatchObject({ amount: 3 });
    expect((await reconcileAudit(t))[0]!.meta).toMatchObject({ trigger: 'nightly', snapshotsSet: 1, created: 1 });
  });
});

describe('statistics reset removes the matching points', () => {
  async function twoCompletions() {
    const f = await fixture({ now: '2026-09-14T06:00:00.000Z' });
    const monday = await f.occurrence('2026-09-14');
    expect((await f.call('PATCH', `/api/occurrences/${monday}`, { action: 'complete' })).statusCode).toBe(200);
    f.t.clock.set('2026-09-16T08:00:00.000Z');
    const wednesday = await f.occurrence('2026-09-16');
    expect((await f.call('PATCH', `/api/occurrences/${wednesday}`, { action: 'complete' })).statusCode).toBe(200);
    expect(await findPointEntries(f.t.db)).toHaveLength(2);
    return { ...f, monday, wednesday };
  }
  const resetAudit = (t: TestApp) => t.db.collection(COLLECTIONS.auditLog).find({ entity: 'settings', action: 'reset' }).sort({ _id: 1 }).toArray();

  it('purging before a day removes only the entries before it and records the count in the one reset entry', async () => {
    const { t, call, monday, wednesday } = await twoCompletions();
    const res = await call('DELETE', '/api/stats?before=2026-09-16');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toMatchObject({ deletedOccurrences: 2, removedPointEntries: 1 });
    const keys = (await findPointEntries(t.db)).map((e) => e.key);
    expect(keys).toEqual([executionKey(new ObjectId(wednesday))]);
    expect(keys).not.toContain(executionKey(new ObjectId(monday)));
    const audit = await resetAudit(t);
    expect(audit).toHaveLength(1);
    expect(audit[0]!.meta).toMatchObject({ scoped: true, removedPointEntries: 1 });
    // No audit entry per removed ledger entry.
    expect(await t.db.collection(COLLECTIONS.auditLog).countDocuments({ entity: 'points', action: 'delete' })).toBe(0);
    expect((await findOccurrenceById(t.db, new ObjectId(wednesday)))!.pointsSnapshot).toBe(3);
  });

  it('starting over removes every entry, clears the snapshots and records the count', async () => {
    const { t, call, wednesday } = await twoCompletions();
    const res = await call('DELETE', '/api/stats');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toMatchObject({ deletedOccurrences: 2, resetOccurrences: 1, removedPointEntries: 2 });
    expect(await findPointEntries(t.db)).toEqual([]);
    expect(await findOccurrenceById(t.db, new ObjectId(wednesday))).toMatchObject({ status: 'open', pointsSnapshot: null });
    const audit = await resetAudit(t);
    expect(audit).toHaveLength(1);
    expect(audit[0]!.meta).toMatchObject({ scoped: false, removedPointEntries: 2 });
    // Nothing left to reconcile: the reset leaves ledger and occurrences consistent.
    const capture = await captureWrites(t, () => reconcilePoints(t.systemCtx(), 'nightly'));
    expect(capture.writes).toEqual([]);
  });
});

describe('import rebuilds the ledger', () => {
  it('exports schema version 3 without the ledger', async () => {
    const { t, occurrence, call } = await fixture();
    expect((await call('PATCH', `/api/occurrences/${await occurrence('2026-09-14')}`, { action: 'complete' })).statusCode).toBe(200);
    const file = (await t.app.inject({ method: 'GET', url: '/api/export/json' })).json<ExportFile>();
    expect(file.schemaVersion).toBe(3);
    expect(Object.keys(file.collections)).not.toContain('pointEntries');
    expect(file.collections.tasks[0]).toHaveProperty('points');
    expect(file.collections.occurrences.some((o) => o.pointsSnapshot === 3)).toBe(true);
  });

  it('rebuilds the ledger from a version-3 file and drops the ledger of the data it replaces', async () => {
    const source = await fixture();
    const monday = await source.occurrence('2026-09-14');
    expect((await source.call('PATCH', `/api/occurrences/${monday}`, { action: 'complete' })).statusCode).toBe(200);
    const file = (await source.t.app.inject({ method: 'GET', url: '/api/export/json' })).json<ExportFile>();

    const target = await fixture();
    expect((await target.call('PATCH', `/api/occurrences/${await target.occurrence('2026-09-15')}`, { action: 'complete', completedBy: target.p2._id.toHexString() })).statusCode).toBe(200);
    const res = await target.t.app.inject({
      method: 'POST',
      url: '/api/import/json?mode=replace&confirm=true',
      headers: asProfile(target.p1),
      payload: file as unknown as Record<string, unknown>,
    });
    expect(res.statusCode, res.body).toBe(200);
    const entries = await findPointEntries(target.t.db);
    expect(entries.map((e) => e.key)).toEqual([executionKey(new ObjectId(monday))]);
    expect(entries[0]).toMatchObject({ amount: 3, source: 'backfill' });
    const audit = await reconcileAudit(target.t);
    expect(audit).toHaveLength(1);
    expect(audit[0]!.meta).toMatchObject({ trigger: 'import', created: 1, updated: 0, removed: 0, tasksDefaulted: 0, snapshotsSet: 0 });
  });

  it('rebuilds a version-2 file, which has no points, with the same defaults as at startup', async () => {
    const source = await fixture();
    const monday = await source.occurrence('2026-09-14');
    const tuesday = await source.occurrence('2026-09-15');
    expect((await source.call('PATCH', `/api/occurrences/${monday}`, { action: 'complete' })).statusCode).toBe(200);
    expect((await source.call('PATCH', `/api/occurrences/${tuesday}`, { action: 'complete', completedBy: source.p1._id.toHexString() })).statusCode).toBe(200);
    const legacy = structuredClone((await source.t.app.inject({ method: 'GET', url: '/api/export/json' })).json<ExportFile>());
    legacy.schemaVersion = 2 as never;
    for (const doc of legacy.collections.tasks) delete doc.points;
    for (const doc of legacy.collections.occurrences) delete doc.pointsSnapshot;
    // An old audit log has no points entries either.
    legacy.collections.auditLog = legacy.collections.auditLog.filter((entry) => entry.entity !== 'points');

    const empty = await createTestApp({ seed: false });
    apps.push(empty);
    const parsed = parseImport(legacy);
    expect(parsed.schemaVersion).toBe(2);
    await importData(empty.systemCtx(), parsed);

    const after = await readAllCollections(empty.db);
    expect(after.tasks.every((task) => task.points === 3)).toBe(true);
    const done = after.occurrences.filter((o) => o.status === 'done');
    expect(done).toHaveLength(2);
    expect(done.every((o) => o.pointsSnapshot === 3)).toBe(true);
    const entries = await findPointEntries(empty.db);
    expect(entries.map((e) => [e.key, e.personId.toHexString(), e.amount]).sort()).toEqual(
      [
        [executionKey(new ObjectId(monday)), source.p1._id.toHexString(), 3],
        [executionKey(new ObjectId(tuesday)), source.p1._id.toHexString(), 3],
      ].sort(),
    );
    const audit = await reconcileAudit(empty);
    expect(audit).toHaveLength(1);
    expect(audit[0]!.meta).toMatchObject({ trigger: 'import', tasksDefaulted: 1, snapshotsSet: 2, created: 2 });
    expect(after.auditLog.filter((e) => e.entity === 'import')).toHaveLength(1);

    const again = await captureWrites(empty, () => reconcilePoints(empty.systemCtx(), 'startup'));
    expect(again.writes).toEqual([]);
    expect(again.auditInserts).toBe(0);
  });
});
