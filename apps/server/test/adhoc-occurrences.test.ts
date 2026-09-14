import type { OccurrenceView } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findTaskById } from '../src/data/tasks.ts';
import type { UserDoc } from '../src/data/users.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

// Wednesday 16 Sep 2026; the nightly run generates cycles 0 (14 Sep–11 Oct) and 1 (12 Oct–8 Nov).
let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
let ramen: string;

const post = (payload: Record<string, unknown>, headers: Record<string, string> = asProfile(p1)) =>
  t.app.inject({ method: 'POST', url: '/api/occurrences', headers, payload });

beforeAll(async () => {
  t = await createTestApp();
  [p1, p2] = await seededUsers(t);
  const room = await seededRoom(t, 'Woonkamer');
  const res = await t.app.inject({
    method: 'POST',
    url: '/api/tasks',
    headers: asProfile(p1),
    payload: { name: 'Ramen lappen', roomId: room._id.toHexString(), intervalKey: 'quarter', durationMinutes: 60, defaultAssigneeId: p2._id.toHexString() },
  });
  ramen = res.json<{ _id: string }>()._id;
  expect((await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(p1) })).statusCode).toBe(200);
});

afterAll(async () => {
  await t.close();
});

describe('POST /api/occurrences (ad-hoc)', () => {
  it('plans a task on a day with the default assignee, audited as create from the UI', async () => {
    const { result, entries } = await expectAudited(t, () => post({ taskId: ramen, date: '2026-09-19' }), {
      entity: 'occurrence',
      action: 'create',
      source: 'ui',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(201);
    expect(result.json<OccurrenceView>()).toMatchObject({
      taskId: ramen,
      date: '2026-09-19',
      plannedDate: '2026-09-19',
      assigneeId: p2._id.toHexString(),
      status: 'open',
      origin: 'adhoc',
      planId: null,
      taskNameSnapshot: 'Ramen lappen',
      durationMinutesSnapshot: 60,
      isOverdue: false,
      movedFrom: null,
    });
    expect(entries[0]!.actorId).toEqual(p1._id);
    expect(entries[0]!.meta).toEqual({ origin: 'adhoc' });
  });

  it('accepts an explicit "wie dan ook"', async () => {
    const res = await post({ taskId: ramen, date: '2026-09-20', assigneeId: null });
    expect(res.statusCode).toBe(201);
    expect(res.json<OccurrenceView>().assigneeId).toBeNull();
  });

  it('refuses a second occurrence of the same task on the same day without writing', async () => {
    const { result } = await expectAudited(t, () => post({ taskId: ramen, date: '2026-09-19' }), {
      entity: 'occurrence',
      action: 'create',
      count: 0,
    });
    expect(result.statusCode).toBe(409);
    expect(result.json()).toMatchObject({ code: 'occurrence_exists' });
  });

  it('only plans within generated cycles', async () => {
    const res = await post({ taskId: ramen, date: '2026-12-01' });
    expect(res.statusCode).toBe(409);
    expect(res.json()).toMatchObject({ code: 'cycle_not_generated', date: '2026-12-01' });
  });

  it('supports "nu gedaan": create for today and complete it', async () => {
    const created = await post({ taskId: ramen, date: '2026-09-16', assigneeId: p1._id.toHexString() });
    expect(created.statusCode).toBe(201);
    const done = await t.app.inject({
      method: 'PATCH',
      url: `/api/occurrences/${created.json<OccurrenceView>()._id}`,
      headers: asProfile(p1),
      payload: { action: 'complete' },
    });
    expect(done.statusCode).toBe(200);
    expect((await findTaskById(t.db, new ObjectId(ramen)))?.lastCompletedAt).toEqual(t.clock.now());
    const due = await t.app.inject({ method: 'GET', url: '/api/due' });
    expect(due.json<{ taskId: string; daysSince: number }[]>().find((i) => i.taskId === ramen)?.daysSince).toBe(0);
  });

  it('validates task, assignee, date and profile', async () => {
    expect((await post({ taskId: '0123456789abcdef01234567', date: '2026-09-21' })).statusCode).toBe(400);
    expect((await post({ taskId: ramen, date: '2026-09-21', assigneeId: '0123456789abcdef01234567' })).statusCode).toBe(400);
    expect((await post({ taskId: ramen, date: '21-09-2026' })).statusCode).toBe(400);
    expect((await post({ taskId: ramen, date: '2026-09-21' }, {})).statusCode).toBe(400);

    await t.app.inject({ method: 'PATCH', url: `/api/tasks/${ramen}`, headers: asProfile(p1), payload: { active: false } });
    const inactive = await post({ taskId: ramen, date: '2026-09-22' });
    expect(inactive.statusCode).toBe(400);
    expect(inactive.json()).toMatchObject({ details: [{ field: 'taskId', message: 'inactive_task' }] });
  });
});
