import { fromDayKey, type OccurrenceView } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { findOccurrenceById } from '../src/data/occurrences.ts';
import {
  deletePointEntry,
  executionKey,
  findPointEntries,
  findPointEntryByKey,
  insertPointEntry,
} from '../src/data/points.ts';
import { findTaskById } from '../src/data/tasks.ts';
import type { UserDoc } from '../src/data/users.ts';
import { syncExecutionPoints } from '../src/domain/points.ts';
import { captureWrites, expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

/**
 * ADR-0011: the ledger is a keyed projection of executions. These tests drive it through the
 * HTTP routes and check the single `execution:<occurrenceId>` entry and its audit trail.
 * Wednesday 16 Sep 2026 (the week of Monday 14 Sep); the nightly run generates cycles 0 and 1.
 */
let t: TestApp;
let p1: UserDoc; // administrator
let p2: UserDoc;
let roomId: string;
let vacuum: string; // 30 minutes, default points: 3
let heavy: string; // explicit 8 points
let free: string; // explicit 0 points

const TODAY = '2026-09-16';
const key = (n: number) => `points-request-key-${String(n).padStart(4, '0')}`;

const call = (method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', url: string, payload?: Record<string, unknown>, actor = p1) =>
  t.app.inject({ method, url, headers: asProfile(actor), ...(payload ? { payload } : {}) });
const patch = (id: string, payload: Record<string, unknown>, actor = p1) =>
  call('PATCH', `/api/occurrences/${id}`, payload, actor);

async function createTask(name: string, extra: Record<string, unknown> = {}, durationMinutes = 30): Promise<string> {
  const res = await call('POST', '/api/tasks', { name, roomId, intervalKey: '1w', durationMinutes, ...extra });
  expect(res.statusCode, res.body).toBe(201);
  return res.json<{ _id: string }>()._id;
}

async function occurrenceOn(date: string, taskId: string): Promise<string> {
  const res = await t.app.inject({ method: 'GET', url: `/api/occurrences?from=${date}&to=${date}` });
  const occ = res.json<OccurrenceView[]>().find((o) => o.taskId === taskId);
  if (!occ) throw new Error(`no occurrence of ${taskId} on ${date}`);
  return occ._id;
}

const entryOf = (occurrenceId: string) => findPointEntryByKey(t.db, executionKey(new ObjectId(occurrenceId)));
const ledgerSize = async () => (await findPointEntries(t.db)).length;
const pointsAudit = (occurrenceId?: string) =>
  t.db
    .collection(COLLECTIONS.auditLog)
    .find({ entity: 'points', ...(occurrenceId ? { 'meta.occurrenceId': new ObjectId(occurrenceId) } : {}) })
    .sort({ _id: 1 })
    .toArray();

beforeAll(async () => {
  t = await createTestApp();
  [p1, p2] = await seededUsers(t);
  roomId = (await seededRoom(t, 'Woonkamer'))._id.toHexString();
  vacuum = await createTask('Stofzuigen');
  heavy = await createTask('Ramen lappen', { points: 8 });
  free = await createTask('Planten water geven', { points: 0 }, 10);
  const plan = (await findActivePlan(t.db))!;
  // Four weeks of Wednesday (p1), Thursday (p2) and Friday (nobody) occurrences for each task.
  const slots = [vacuum, heavy, free].flatMap((taskId) =>
    [0, 1, 2, 3].flatMap((weekIndex) => [
      { taskId, weekIndex, weekday: 3, assigneeId: p1._id.toHexString() },
      { taskId, weekIndex, weekday: 4, assigneeId: p2._id.toHexString() },
      { taskId, weekIndex, weekday: 5, assigneeId: null },
    ]),
  );
  const put = await call('PUT', `/api/cycle-plans/${plan._id.toHexString()}/slots`, { slots });
  expect(put.statusCode, put.body).toBe(200);
  expect((await call('POST', '/api/jobs/nightly')).statusCode).toBe(200);
});

afterAll(async () => {
  await t.close();
});

describe('a check-off creates exactly one ledger entry', () => {
  it('credits the person with the task points, and a second complete changes nothing', async () => {
    const id = await occurrenceOn('2026-09-16', vacuum);
    const { result, entries } = await expectAudited(t, () => patch(id, { action: 'complete' }), {
      entity: 'points',
      action: 'create',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json<OccurrenceView>().pointsSnapshot).toBe(3);

    const entry = (await entryOf(id))!;
    expect(entry).toMatchObject({
      key: `execution:${id}`,
      kind: 'execution',
      personId: p1._id,
      amount: 3,
      date: fromDayKey(TODAY),
      weekStart: fromDayKey('2026-09-14'),
      occurrenceId: new ObjectId(id),
      taskId: new ObjectId(vacuum),
      titleSnapshot: 'Stofzuigen',
      source: 'live',
    });
    expect(entries[0]!.actorId).toEqual(p1._id);
    expect(entries[0]!.entityId).toEqual(entry._id);
    expect(entries[0]!.meta).toEqual({ occurrenceId: new ObjectId(id), reason: 'complete' });
    expect(entries[0]!.after).toMatchObject({ key: `execution:${id}`, personId: p1._id, amount: 3 });

    const again = await captureWrites(t, () => patch(id, { action: 'complete' }));
    expect(again.result.statusCode).toBe(409);
    expect(again.writes).toEqual([]);
    expect(again.auditInserts).toBe(0);
    expect((await findPointEntries(t.db)).filter((e) => e.key === `execution:${id}`)).toHaveLength(1);
  });

  it('writes an occurrence and a points audit entry, and no task entry when lastCompletedAt does not move', async () => {
    const id = await occurrenceOn('2026-09-23', vacuum);
    const capture = await captureWrites(t, () => patch(id, { action: 'complete' }));
    expect(capture.result.statusCode).toBe(200);
    expect(capture.auditInserts).toBe(2);
    expect(await pointsAudit(id)).toHaveLength(1);
  });

  it('keeps the unique key: a second insert of the same key is reported, not written', async () => {
    const id = await occurrenceOn('2026-09-30', vacuum);
    expect((await patch(id, { action: 'complete' })).statusCode).toBe(200);
    const stored = (await entryOf(id))!;
    const duplicate = await insertPointEntry(t.systemCtx(), stored.key, 'execution', stored, 'live', {
      occurrenceId: new ObjectId(id),
      reason: 'complete',
    });
    expect(duplicate).toEqual({ inserted: false });
    expect(await pointsAudit(id)).toHaveLength(1);
    const indexes = await t.db.collection(COLLECTIONS.pointEntries).indexes();
    expect(indexes.find((index) => index.name === 'pointEntries_key_unique')).toMatchObject({
      unique: true,
      key: { key: 1 },
    });
  });

  it('earns nothing, and audits nothing, for a task of 0 points', async () => {
    const id = await occurrenceOn('2026-09-16', free);
    const capture = await captureWrites(t, () => patch(id, { action: 'complete' }));
    expect(capture.result.statusCode).toBe(200);
    expect(capture.result.json<OccurrenceView>().pointsSnapshot).toBe(0);
    expect(await entryOf(id)).toBeNull();
    expect(await pointsAudit(id)).toEqual([]);
  });

  it('credits the person who performed the work: on behalf, take over and unassigned', async () => {
    const onBehalf = await occurrenceOn('2026-09-17', vacuum); // assigned to p2, p1 presses
    expect((await patch(onBehalf, { action: 'complete' })).statusCode).toBe(400);
    expect(await entryOf(onBehalf)).toBeNull();
    expect((await patch(onBehalf, { action: 'complete', completedBy: p2._id.toHexString() })).statusCode).toBe(200);
    expect((await entryOf(onBehalf))?.personId).toEqual(p2._id);

    const takeOver = await occurrenceOn('2026-09-24', vacuum); // assigned to p2, p1 takes over
    expect((await patch(takeOver, { action: 'complete', takeOver: true })).statusCode).toBe(200);
    expect((await entryOf(takeOver))?.personId).toEqual(p1._id);

    const unassigned = await occurrenceOn('2026-09-18', vacuum);
    expect((await patch(unassigned, { action: 'complete' }, p2)).statusCode).toBe(200);
    expect((await entryOf(unassigned))?.personId).toEqual(p2._id);
  });
});

describe('uncomplete and a snapshot per completion', () => {
  it('deletes the entry on uncomplete, clears the snapshot, and re-completing uses the current points', async () => {
    const id = await occurrenceOn('2026-10-07', heavy);
    expect((await patch(id, { action: 'complete' })).statusCode).toBe(200);
    expect((await entryOf(id))?.amount).toBe(8);

    const { result, entries } = await expectAudited(t, () => patch(id, { action: 'uncomplete' }), {
      entity: 'points',
      action: 'delete',
      count: 1,
    });
    expect(result.json<OccurrenceView>().pointsSnapshot).toBeNull();
    expect(entries[0]!.meta).toEqual({ occurrenceId: new ObjectId(id), reason: 'uncomplete' });
    expect(entries[0]!.before).toMatchObject({ key: `execution:${id}`, amount: 8, personId: p1._id });
    expect(await entryOf(id)).toBeNull();

    expect((await call('PATCH', `/api/tasks/${heavy}`, { points: 5 })).statusCode).toBe(200);
    expect((await patch(id, { action: 'complete' })).statusCode).toBe(200);
    expect((await entryOf(id))?.amount).toBe(5);
    expect((await call('PATCH', `/api/tasks/${heavy}`, { points: 8 })).statusCode).toBe(200);
  });
});

describe('recorded work', () => {
  it('records an extra execution with the task points for the actor, and a retract deletes the entry', async () => {
    const { result, entries } = await expectAudited(
      t,
      () => call('POST', '/api/occurrences', { taskId: heavy, date: TODAY, done: true, requestId: key(1) }),
      { entity: 'points', action: 'create', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(201);
    const created = result.json<OccurrenceView>();
    expect(created.pointsSnapshot).toBe(8);
    expect(entries[0]!.meta).toEqual({ occurrenceId: new ObjectId(created._id), reason: 'recorded' });
    expect(await entryOf(created._id)).toMatchObject({ personId: p1._id, amount: 8, taskId: new ObjectId(heavy) });

    const undo = await expectAudited(t, () => call('POST', `/api/occurrences/${created._id}/retract`), {
      entity: 'points',
      action: 'delete',
      count: 1,
    });
    expect(undo.result.statusCode, undo.result.body).toBe(200);
    expect(undo.entries[0]!.meta).toEqual({ occurrenceId: new ObjectId(created._id), reason: 'retract' });
    expect(await entryOf(created._id)).toBeNull();
  });

  it('credits a one-off task of 30 minutes with 3 points, to the chosen person', async () => {
    const mine = await call('POST', '/api/occurrences/one-off', {
      name: 'Kast opruimen',
      durationMinutes: 30,
      date: TODAY,
      done: true,
      requestId: key(2),
    });
    expect(mine.statusCode, mine.body).toBe(201);
    expect(mine.json<OccurrenceView>().pointsSnapshot).toBe(3);
    expect(await entryOf(mine.json<OccurrenceView>()._id)).toMatchObject({
      personId: p1._id,
      amount: 3,
      taskId: null,
      titleSnapshot: 'Kast opruimen',
    });

    const theirs = await call('POST', '/api/occurrences/one-off', {
      name: 'Zolder vegen',
      durationMinutes: 95,
      date: TODAY,
      done: true,
      assigneeId: p2._id.toHexString(),
      requestId: key(3),
    });
    expect(await entryOf(theirs.json<OccurrenceView>()._id)).toMatchObject({ personId: p2._id, amount: 10 });
  });

  it('does not earn points before it is done', async () => {
    const planned = await call('POST', '/api/occurrences', { taskId: heavy, date: '2026-09-19' });
    expect(planned.statusCode, planned.body).toBe(201);
    expect(planned.json<OccurrenceView>().pointsSnapshot).toBeNull();
    expect(await entryOf(planned.json<OccurrenceView>()._id)).toBeNull();
  });

  it('writes nothing when a known requestId is replayed, and repairs a lost entry', async () => {
    const payload = { taskId: vacuum, date: TODAY, done: true, requestId: key(4) };
    const first = await call('POST', '/api/occurrences', payload);
    expect(first.statusCode).toBe(201);
    const id = first.json<OccurrenceView>()._id;
    const sizeBefore = await ledgerSize();

    const replay = await captureWrites(t, () => call('POST', '/api/occurrences', payload));
    expect(replay.result.statusCode).toBe(200);
    expect(replay.writes).toEqual([]);
    expect(replay.auditInserts).toBe(0);
    expect(await ledgerSize()).toBe(sizeBefore);

    // A crash between the occurrence write and the ledger write left no entry: the replay restores it.
    await deletePointEntry(t.systemCtx(), (await entryOf(id))!, {
      occurrenceId: new ObjectId(id),
      reason: 'correction',
    });
    const repaired = await expectAudited(t, () => call('POST', '/api/occurrences', payload), {
      entity: 'points',
      action: 'create',
      count: 1,
    });
    expect(repaired.result.statusCode).toBe(200);
    expect((await entryOf(id))?.amount).toBe(3);
  });
});

describe('administrator corrections', () => {
  it('moves the entry to another person and date with one audited update, and leaves no-ops alone', async () => {
    const id = await occurrenceOn('2026-10-01', vacuum); // Thursday, assigned to p2
    expect((await patch(id, { action: 'complete', completedBy: p2._id.toHexString() })).statusCode).toBe(200);
    const stored = (await entryOf(id))!;
    expect(stored.personId).toEqual(p2._id);
    const completedAt = (await findOccurrenceById(t.db, new ObjectId(id)))!.completedAt!.toISOString();
    const correction = { action: 'edit_completion', date: '2026-09-29', completedAt, completedBy: p1._id.toHexString() };

    const edit = await expectAudited(t, () => patch(id, correction), {
      entity: 'points',
      action: 'update',
      count: 1,
    });
    expect(edit.result.statusCode, edit.result.body).toBe(200);
    const moved = (await entryOf(id))!;
    expect(moved._id).toEqual(stored._id);
    expect(moved).toMatchObject({
      personId: p1._id,
      amount: 3,
      date: fromDayKey('2026-09-29'),
      weekStart: fromDayKey('2026-09-28'),
      source: 'live',
    });
    // The diff lists changed fields only, so the title and the amount travel in the meta for the history feed.
    expect(edit.entries[0]!.meta).toEqual({
      occurrenceId: new ObjectId(id),
      reason: 'correction',
      titleSnapshot: 'Stofzuigen',
      amount: 3,
    });
    expect(edit.entries[0]!.before).toEqual({ personId: p2._id, date: fromDayKey('2026-10-01') });
    expect(edit.entries[0]!.after).toEqual({ personId: p1._id, date: fromDayKey('2026-09-29') });

    // Repeating the same correction changes nothing, so nothing is written or audited.
    const same = await captureWrites(t, () => patch(id, correction));
    expect(same.result.statusCode).toBe(200);
    expect(same.writes).toEqual([]);
    expect(same.auditInserts).toBe(0);

    // Only the time of day changes: the occurrence is audited, the ledger is not touched.
    const later = new Date(new Date(completedAt).getTime() + 60_000).toISOString();
    const timeOnly = await captureWrites(t, () => patch(id, { ...correction, completedAt: later }));
    expect(timeOnly.result.statusCode).toBe(200);
    expect(timeOnly.writes.map((w) => w.collection)).not.toContain(COLLECTIONS.pointEntries);
    expect(await pointsAudit(id)).toHaveLength(2); // the create and the one update
  });

  it('deletes the entry when an administrator deletes the completion', async () => {
    const id = await occurrenceOn('2026-10-02', vacuum);
    expect((await patch(id, { action: 'complete' })).statusCode).toBe(200);
    expect(await entryOf(id)).not.toBeNull();
    const { result, entries } = await expectAudited(t, () => call('DELETE', `/api/occurrences/${id}`), {
      entity: 'points',
      action: 'delete',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(200);
    expect(entries[0]!.meta).toEqual({ occurrenceId: new ObjectId(id), reason: 'correction' });
    expect(await entryOf(id)).toBeNull();
  });

  it('refuses a correction by a member and leaves the ledger as it was', async () => {
    const id = await occurrenceOn('2026-10-08', vacuum);
    expect((await patch(id, { action: 'complete', completedBy: p2._id.toHexString() })).statusCode).toBe(200);
    const before = await entryOf(id);
    const res = await patch(
      id,
      {
        action: 'edit_completion',
        date: '2026-10-08',
        completedAt: new Date().toISOString(),
        completedBy: p1._id.toHexString(),
      },
      p2,
    );
    expect(res.statusCode).toBe(403);
    expect(await entryOf(id)).toEqual(before);
  });
});

describe('syncing is idempotent', () => {
  it('writes and audits nothing when the entry already matches', async () => {
    const id = await occurrenceOn('2026-09-25', vacuum); // Friday, nobody
    expect((await patch(id, { action: 'complete' })).statusCode).toBe(200);
    const audit = (await pointsAudit(id)).length;

    const capture = await captureWrites(t, () => syncExecutionPoints(t.systemCtx(), new ObjectId(id), 'correction'));
    expect(capture.result).toBe('unchanged');
    expect(capture.writes).toEqual([]);
    expect(capture.auditInserts).toBe(0);
    expect(await pointsAudit(id)).toHaveLength(audit);
  });

  it('removes an orphaned entry of an occurrence that no longer exists', async () => {
    const id = new ObjectId();
    const created = await insertPointEntry(
      t.systemCtx(),
      executionKey(id),
      'execution',
      {
        personId: p1._id,
        amount: 2,
        date: fromDayKey(TODAY),
        weekStart: fromDayKey('2026-09-14'),
        occurrenceId: id,
        taskId: null,
        titleSnapshot: 'Weg',
      },
      'live',
      { occurrenceId: id, reason: 'correction' },
    );
    expect(created.inserted).toBe(true);
    expect(await syncExecutionPoints(t.systemCtx(), id, 'correction')).toBe('deleted');
    expect(await findPointEntryByKey(t.db, executionKey(id))).toBeNull();
  });
});

describe('task points', () => {
  it('defaults the points of a new task from its duration, within 1..100', async () => {
    const forty = await call('POST', '/api/tasks', { name: 'A', roomId, intervalKey: '1w', durationMinutes: 45 });
    expect(forty.json()).toMatchObject({ points: 5 });
    const quick = await call('POST', '/api/tasks', { name: 'B', roomId, intervalKey: '1w', durationMinutes: 1 });
    expect(quick.json()).toMatchObject({ points: 1 });
    const huge = await call('POST', '/api/tasks', { name: 'C', roomId, intervalKey: '1w', durationMinutes: 5000 });
    expect(huge.json()).toMatchObject({ points: 100 });
    const stored = await findTaskById(t.db, new ObjectId(forty.json<{ _id: string }>()._id));
    expect(stored?.points).toBe(5);
  });

  it.each([0, 1, 100])('accepts %d points on create and on update', async (points) => {
    const created = await call('POST', '/api/tasks', {
      name: `P${points}`,
      roomId,
      intervalKey: '1w',
      durationMinutes: 30,
      points,
    });
    expect(created.statusCode, created.body).toBe(201);
    expect(created.json()).toMatchObject({ points });
    const updated = await call('PATCH', `/api/tasks/${created.json<{ _id: string }>()._id}`, { points: 50 });
    expect(updated.json()).toMatchObject({ points: 50 });
  });

  it.each([-1, 101, 2.5, '3'])('rejects %j points with a validation error naming the field', async (points) => {
    const base = { roomId, intervalKey: '1w', durationMinutes: 30 };
    const create = await captureWrites(t, () => call('POST', '/api/tasks', { name: 'Fout', ...base, points }));
    expect(create.result.statusCode).toBe(400);
    expect(create.result.json()).toMatchObject({ code: 'validation_error', details: [{ field: 'points' }] });
    expect(create.writes).toEqual([]);
    const update = await call('PATCH', `/api/tasks/${vacuum}`, { points });
    expect(update.statusCode).toBe(400);
    expect(update.json()).toMatchObject({ details: [{ field: 'points' }] });
  });

  it('audits a points change as a task update, and a repeat is a no-op', async () => {
    const task = await createTask('Audit', { points: 4 });
    const change = await expectAudited(t, () => call('PATCH', `/api/tasks/${task}`, { points: 6 }), {
      entity: 'task',
      action: 'update',
      count: 1,
    });
    expect(change.entries[0]!.before).toEqual({ points: 4 });
    expect(change.entries[0]!.after).toEqual({ points: 6 });
    const repeat = await captureWrites(t, () => call('PATCH', `/api/tasks/${task}`, { points: 6 }));
    expect(repeat.writes).toEqual([]);
    expect(repeat.auditInserts).toBe(0);
  });

  it('leaves past entries and snapshots alone when the task points change', async () => {
    const id = await occurrenceOn('2026-09-16', heavy);
    expect((await patch(id, { action: 'complete' })).statusCode).toBe(200);
    expect((await entryOf(id))?.amount).toBe(8);
    const entriesBefore = await findPointEntries(t.db);
    const auditBefore = (await pointsAudit()).length;

    const changed = await call('PATCH', `/api/tasks/${heavy}`, { points: 2 });
    expect(changed.json()).toMatchObject({ points: 2 });
    expect(await findPointEntries(t.db)).toEqual(entriesBefore);
    expect((await pointsAudit()).length).toBe(auditBefore);
    expect((await findOccurrenceById(t.db, new ObjectId(id)))?.pointsSnapshot).toBe(8);
    await call('PATCH', `/api/tasks/${heavy}`, { points: 8 });
  });

  it('shows the default for a task from before points existed, and snapshots it on completion', async () => {
    const task = await createTask('Oud', {}, 60);
    // eslint-disable-next-line no-restricted-syntax -- simulates a task document from before points existed
    await t.db.collection(COLLECTIONS.tasks).updateOne({ _id: new ObjectId(task) }, { $unset: { points: '' } });
    const list = await t.app.inject({ method: 'GET', url: '/api/tasks' });
    expect(list.json<{ _id: string; points: number }[]>().find((x) => x._id === task)?.points).toBe(6);

    const adhoc = await call('POST', '/api/occurrences', { taskId: task, date: TODAY, done: true, requestId: key(5) });
    expect(adhoc.json<OccurrenceView>().pointsSnapshot).toBe(6);
    expect((await entryOf(adhoc.json<OccurrenceView>()._id))?.amount).toBe(6);
  });
});
