import { toDayKey, type CompletionResponse, type OccurrenceView } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { findOccurrences } from '../src/data/occurrences.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp | undefined;

afterEach(async () => {
  await t?.close();
  t = undefined;
});

describe('DELETE /api/stats', () => {
  it('starts execution statistics over while preserving people, rooms, tasks and the active plan', async () => {
    t = await createTestApp({ now: '2026-09-14T06:00:00.000Z' });
    const [person] = await seededUsers(t);
    const headers = asProfile(person);
    const room = await seededRoom(t, 'Keuken');
    const taskResponse = await t.app.inject({
      method: 'POST',
      url: '/api/tasks',
      headers,
      payload: { name: 'Aanrecht', roomId: room._id.toHexString(), intervalKey: '1w', durationMinutes: 15 },
    });
    const taskId = taskResponse.json<{ _id: string }>()._id;
    const active = (await findActivePlan(t.db))!;
    await t.app.inject({
      method: 'PUT',
      url: `/api/cycle-plans/${active._id.toHexString()}/slots`,
      headers,
      payload: {
        slots: [
          { taskId, weekIndex: 0, weekday: 1, assigneeId: person._id.toHexString() },
          { taskId, weekIndex: 0, weekday: 3, assigneeId: person._id.toHexString() },
          { taskId, weekIndex: 0, weekday: 4, assigneeId: person._id.toHexString() },
        ],
      },
    });
    await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers });

    const monday = (await t.app.inject({ method: 'GET', url: '/api/occurrences?from=2026-09-14&to=2026-09-14' })).json<OccurrenceView[]>()[0]!;
    await t.app.inject({ method: 'PATCH', url: `/api/occurrences/${monday._id}`, headers, payload: { action: 'complete' } });
    t.clock.set('2026-09-16T08:00:00.000Z');
    const wednesday = (await t.app.inject({ method: 'GET', url: '/api/occurrences?from=2026-09-16&to=2026-09-16' })).json<OccurrenceView[]>()[0]!;
    await t.app.inject({ method: 'PATCH', url: `/api/occurrences/${wednesday._id}`, headers, payload: { action: 'complete' } });

    const countsBefore = await Promise.all([
      t.db.collection(COLLECTIONS.users).countDocuments(),
      t.db.collection(COLLECTIONS.rooms).countDocuments(),
      t.db.collection(COLLECTIONS.tasks).countDocuments(),
      t.db.collection(COLLECTIONS.cyclePlans).countDocuments(),
    ]);
    const response = await t.app.inject({ method: 'DELETE', url: '/api/stats', headers });
    expect(response.statusCode, response.body).toBe(200);
    expect(response.json()).toMatchObject({ deletedOccurrences: 1, resetOccurrences: 1, resetTasks: 1 });

    expect(await Promise.all([
      t.db.collection(COLLECTIONS.users).countDocuments(),
      t.db.collection(COLLECTIONS.rooms).countDocuments(),
      t.db.collection(COLLECTIONS.tasks).countDocuments(),
      t.db.collection(COLLECTIONS.cyclePlans).countDocuments(),
    ])).toEqual(countsBefore);
    expect((await findActivePlan(t.db))!._id).toEqual(active._id);
    expect((await t.db.collection(COLLECTIONS.tasks).findOne({ _id: new ObjectId(taskId) }))!.lastCompletedAt).toBeNull();

    const remaining = await findOccurrences(t.db, {});
    expect(remaining.length).toBeGreaterThanOrEqual(2);
    expect(remaining.every((occurrence) => occurrence.status === 'open')).toBe(true);
    expect(remaining.some((occurrence) => occurrence._id.toHexString() === monday._id)).toBe(false);
    const completion = await t.app.inject({ method: 'GET', url: '/api/stats/completion?cycles=4&groupBy=task' });
    expect(completion.json<CompletionResponse>().rows).toEqual([]);
    expect(await t.db.collection(COLLECTIONS.auditLog).findOne({ entity: 'settings', action: 'reset' })).not.toBeNull();
  });

  it('with `before`, only purges data strictly older than that day and leaves recent completions untouched', async () => {
    t = await createTestApp({ now: '2026-09-14T06:00:00.000Z' }); // Monday, cycle anchor
    const [person] = await seededUsers(t);
    const headers = asProfile(person);
    const room = await seededRoom(t, 'Keuken');
    const taskResponse = await t.app.inject({
      method: 'POST',
      url: '/api/tasks',
      headers,
      payload: { name: 'Aanrecht', roomId: room._id.toHexString(), intervalKey: '1w', durationMinutes: 15 },
    });
    const taskId = taskResponse.json<{ _id: string }>()._id;
    const active = (await findActivePlan(t.db))!;
    await t.app.inject({
      method: 'PUT',
      url: `/api/cycle-plans/${active._id.toHexString()}/slots`,
      headers,
      payload: { slots: [0, 1, 2, 3].map((weekIndex) => ({ taskId, weekIndex, weekday: 3, assigneeId: person._id.toHexString() })) },
    });
    // One nightly run at the anchor already generates the current cycle (0) and the next (1).
    await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers });

    const staleWednesday = (await t.app.inject({ method: 'GET', url: '/api/occurrences?from=2026-09-16&to=2026-09-16' })).json<OccurrenceView[]>()[0]!;
    await t.app.inject({ method: 'PATCH', url: `/api/occurrences/${staleWednesday._id}`, headers, payload: { action: 'complete' } });

    t.clock.set('2026-10-14T08:00:00.000Z'); // Wednesday inside cycle 1
    const recentWednesday = (await t.app.inject({ method: 'GET', url: '/api/occurrences?from=2026-10-14&to=2026-10-14' })).json<OccurrenceView[]>()[0]!;
    await t.app.inject({ method: 'PATCH', url: `/api/occurrences/${recentWednesday._id}`, headers, payload: { action: 'complete' } });

    const response = await t.app.inject({ method: 'DELETE', url: '/api/stats?before=2026-10-12', headers });
    expect(response.statusCode, response.body).toBe(200);
    expect(response.json()).toMatchObject({ deletedOccurrences: 4, resetOccurrences: 0, resetTasks: 0, deletedPastCycles: 1 });

    const remaining = await findOccurrences(t.db, {});
    expect(remaining.every((occurrence) => toDayKey(occurrence.date) >= '2026-10-12')).toBe(true);
    const recent = remaining.find((occurrence) => occurrence._id.toHexString() === recentWednesday._id)!;
    expect(recent.status).toBe('done');
    expect((await t.db.collection(COLLECTIONS.tasks).findOne({ _id: new ObjectId(taskId) }))!.lastCompletedAt).not.toBeNull();
    expect((await t.db.collection(COLLECTIONS.cycles).find({}).toArray()).map((c) => c.index)).toEqual([1]);
    const audit = await t.db.collection(COLLECTIONS.auditLog).findOne({ entity: 'settings', action: 'reset', 'meta.scoped': true });
    expect(audit).not.toBeNull();
  });

  it('rejects a `before` date in the future', async () => {
    t = await createTestApp({ now: '2026-09-14T06:00:00.000Z' });
    const [person] = await seededUsers(t);
    const response = await t.app.inject({
      method: 'DELETE',
      url: '/api/stats?before=2026-09-15',
      headers: asProfile(person),
    });
    expect(response.statusCode).toBe(400);
  });

  it('requires an active profile', async () => {
    t = await createTestApp();
    expect((await t.app.inject({ method: 'DELETE', url: '/api/stats' })).statusCode).toBe(400);
  });
});
