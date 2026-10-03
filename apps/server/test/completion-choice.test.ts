import type { OccurrenceView } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { findOccurrenceById } from '../src/data/occurrences.ts';
import type { UserDoc } from '../src/data/users.ts';
import { captureWrites, expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

/**
 * ADR-0011: `completedBy` is the person credited, and it is never implied. Work of someone else
 * needs an explicit choice; unassigned work and the actor's own work default to the actor.
 * Wednesday 16 Sep 2026; the nightly run generates cycle 0 (14 Sep to 11 Oct).
 */
let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
const occurrences: Record<string, string> = {};

const patch = (id: string, payload: Record<string, unknown>, actor = p1) =>
  t.app.inject({ method: 'PATCH', url: `/api/occurrences/${id}`, headers: asProfile(actor), payload });

async function occurrenceOn(date: string): Promise<string> {
  const res = await t.app.inject({ method: 'GET', url: `/api/occurrences?from=${date}&to=${date}` });
  const [occ] = res.json<OccurrenceView[]>();
  if (!occ) throw new Error(`no occurrence on ${date}`);
  return occ._id;
}

beforeAll(async () => {
  t = await createTestApp();
  [p1, p2] = await seededUsers(t);
  const room = await seededRoom(t, 'Woonkamer');
  const created = await t.app.inject({
    method: 'POST',
    url: '/api/tasks',
    headers: asProfile(p1),
    payload: { name: 'Ramen lappen', roomId: room._id.toHexString(), intervalKey: '1w', durationMinutes: 20 },
  });
  const taskId = created.json<{ _id: string }>()._id;
  const plan = (await findActivePlan(t.db))!;
  // One task, one occurrence a day, so a day identifies the occurrence: Wed p2, Thu p2, Fri p2, Sat p1, Sun nobody.
  const slot = (weekday: number, assigneeId: string | null) => ({ taskId, weekIndex: 0, weekday, assigneeId });
  const put = await t.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${plan._id.toHexString()}/slots`,
    headers: asProfile(p1),
    payload: {
      slots: [
        slot(3, p2._id.toHexString()),
        slot(4, p2._id.toHexString()),
        slot(5, p2._id.toHexString()),
        slot(6, p1._id.toHexString()),
        slot(0, null),
      ],
    },
  });
  expect(put.statusCode, put.body).toBe(200);
  expect((await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(p1) })).statusCode).toBe(200);
  occurrences.implicit = await occurrenceOn('2026-09-16');
  occurrences.takeOver = await occurrenceOn('2026-09-17');
  occurrences.onBehalf = await occurrenceOn('2026-09-18');
  occurrences.own = await occurrenceOn('2026-09-19');
  occurrences.unassigned = await occurrenceOn('2026-09-20');
});

afterAll(async () => {
  await t.close();
});

describe('completing work that is assigned to someone else', () => {
  it('rejects an implicit check-off with completion_choice_required and writes nothing', async () => {
    const capture = await captureWrites(t, () => patch(occurrences.implicit!, { action: 'complete' }));
    expect(capture.result.statusCode).toBe(400);
    expect(capture.result.json()).toMatchObject({
      code: 'validation_error',
      details: [{ field: 'completedBy', message: 'completion_choice_required' }],
    });
    expect(capture.writes).toEqual([]);
    expect(capture.auditInserts).toBe(0);
    expect((await findOccurrenceById(t.db, new ObjectId(occurrences.implicit)))?.status).toBe('open');
  });

  it('takes over: the actor is credited and becomes the assignee', async () => {
    const { result, entries } = await expectAudited(
      t,
      () => patch(occurrences.takeOver!, { action: 'complete', takeOver: true }),
      { entity: 'occurrence', action: 'complete', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json()).toMatchObject({
      status: 'done',
      completedBy: p1._id.toHexString(),
      assigneeId: p1._id.toHexString(),
    });
    expect(entries[0]!.meta).toMatchObject({ takenOver: true, previousAssigneeId: p2._id });
  });

  it('on behalf: the named person is credited and the assignee is unchanged', async () => {
    const { result, entries } = await expectAudited(
      t,
      () => patch(occurrences.onBehalf!, { action: 'complete', completedBy: p2._id.toHexString() }),
      { entity: 'occurrence', action: 'complete', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json()).toMatchObject({
      status: 'done',
      completedBy: p2._id.toHexString(),
      assigneeId: p2._id.toHexString(),
    });
    expect(entries[0]!.actorId).toEqual(p1._id);
    expect(entries[0]!.meta).toMatchObject({ completedBy: p2._id, wasAssignee: true });
  });

  it('rejects completedBy together with takeOver as completion_choice_conflict', async () => {
    const res = await patch(occurrences.implicit!, {
      action: 'complete',
      completedBy: p2._id.toHexString(),
      takeOver: true,
    });
    expect(res.statusCode).toBe(400);
    expect(res.json()).toMatchObject({
      code: 'validation_error',
      details: [{ field: 'completedBy', message: 'completion_choice_conflict' }],
    });
    expect((await findOccurrenceById(t.db, new ObjectId(occurrences.implicit)))?.status).toBe('open');
  });

  it('does not need a choice for work of the actor: the actor is credited', async () => {
    const res = await patch(occurrences.own!, { action: 'complete' });
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toMatchObject({ completedBy: p1._id.toHexString(), assigneeId: p1._id.toHexString() });
  });

  it('does not need a choice for unassigned work: the actor is credited and claims it', async () => {
    const res = await patch(occurrences.unassigned!, { action: 'complete' }, p2);
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toMatchObject({ completedBy: p2._id.toHexString(), assigneeId: p2._id.toHexString() });
  });

  it('answers a second complete with 409 before asking for a choice', async () => {
    const res = await patch(occurrences.takeOver!, { action: 'complete' }, p2);
    expect(res.statusCode).toBe(409);
    expect(res.json()).toMatchObject({ code: 'invalid_transition' });
  });
});
