import type { LightMyRequestResponse } from 'fastify';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan, findPlanById } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { countOccurrences } from '../src/data/occurrences.ts';
import type { UserDoc } from '../src/data/users.ts';
import { MockProvider } from '../src/domain/ai/providers/mock.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp | undefined;

afterEach(async () => {
  await t?.close();
  t = undefined;
});

interface Ctx {
  t: TestApp;
  p1: UserDoc;
  p2: UserDoc;
  weekly: string;
  twice: string;
  ramen: string;
  activeId: string;
  draftId: string;
  proposalId: string;
  call(method: 'GET' | 'POST', url: string, payload?: Record<string, unknown>): Promise<LightMyRequestResponse>;
}

/**
 * Active plan:  weekly  w0 Mon P1, w1 Mon P1 · twice w0 Wed P2, w2 Sat P2
 * AI proposal:  weekly  w0 Mon P1, w1 Tue P2 · twice w0 Wed P1           · ramen w2 Fri P1
 */
async function setup(): Promise<Ctx> {
  // Filled in once the tasks exist; the responder only reads them when the proposal is requested.
  const ids = { weekly: '', twice: '', ramen: '', P1: '', P2: '' };
  const provider = new MockProvider({
    responders: {
      'plan-proposal': () =>
        JSON.stringify({
          slots: [
            { taskId: ids.weekly, weekIndex: 0, weekday: 1, assigneeId: ids.P1 },
            { taskId: ids.weekly, weekIndex: 1, weekday: 2, assigneeId: ids.P2 },
            { taskId: ids.twice, weekIndex: 0, weekday: 3, assigneeId: ids.P1 },
            { taskId: ids.ramen, weekIndex: 2, weekday: 5, assigneeId: null },
          ],
          rationale: ['Week 1.', 'Week 2.', 'Week 3.', 'Week 4.'],
        }),
    },
  });
  const app = await createTestApp({ aiProvider: provider, now: '2026-09-14T06:00:00.000Z' });
  t = app;
  const [p1, p2] = await seededUsers(app);
  const headers = asProfile(p1);
  const call = (method: 'GET' | 'POST', url: string, payload?: Record<string, unknown>) =>
    app.app.inject({ method, url, headers, ...(payload ? { payload } : {}) });
  const room = await seededRoom(app, 'Badkamer');
  const task = async (name: string, intervalKey: string, durationMinutes: number) =>
    (
      await app.app.inject({
        method: 'POST',
        url: '/api/tasks',
        headers,
        payload: { name, roomId: room._id.toHexString(), intervalKey, durationMinutes },
      })
    ).json<{ _id: string }>()._id;
  // This suite tests proposal diffs, not cycle completion. Optional tasks keep
  // the deliberately sparse slot layout below unchanged.
  const weekly = await task('Badkamer schoonmaken', 'quarter', 30);
  const twice = await task('Wastafel', 'quarter', 10);
  const ramen = await task('Ramen lappen', 'quarter', 60);
  Object.assign(ids, { weekly, twice, ramen, P1: p1._id.toHexString(), P2: p2._id.toHexString() });

  const activeId = (await findActivePlan(app.db))!._id.toHexString();
  const put = await app.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${activeId}/slots`,
    headers,
    payload: {
      slots: [
        { taskId: weekly, weekIndex: 0, weekday: 1, assigneeId: ids.P1 },
        { taskId: weekly, weekIndex: 1, weekday: 1, assigneeId: ids.P1 },
        { taskId: twice, weekIndex: 0, weekday: 3, assigneeId: ids.P2 },
        { taskId: twice, weekIndex: 2, weekday: 6, assigneeId: ids.P2 },
      ],
    },
  });
  expect(put.statusCode, put.body).toBe(200);
  const proposal = await call('POST', '/api/ai/propose-plan');
  expect(proposal.statusCode, proposal.body).toBe(200);
  const { planId: draftId, proposalId } = proposal.json<{ planId: string; proposalId: string }>();
  return { t: app, p1, p2, weekly, twice, ramen, activeId, draftId, proposalId, call };
}

describe('GET /api/cycle-plans/:id/diff', () => {
  it('lists added, removed and moved slots against the active plan', async () => {
    const c = await setup();
    const res = await c.call('GET', `/api/cycle-plans/${c.draftId}/diff?against=active`);
    expect(res.statusCode, res.body).toBe(200);
    const diff = res.json<{
      planId: string;
      againstPlanId: string;
      added: unknown[];
      removed: unknown[];
      moved: unknown[];
      unchanged: number;
    }>();
    const P1 = c.p1._id.toHexString();
    const P2 = c.p2._id.toHexString();
    expect(diff).toMatchObject({ planId: c.draftId, againstPlanId: c.activeId, unchanged: 1 });
    expect(diff.added).toEqual([
      { taskId: c.ramen, taskName: 'Ramen lappen', roomName: 'Badkamer', durationMinutes: 60, weekIndex: 2, weekday: 5, assigneeId: P1 },
    ]);
    expect(diff.removed).toEqual([
      { taskId: c.twice, taskName: 'Wastafel', roomName: 'Badkamer', durationMinutes: 10, weekIndex: 2, weekday: 6, assigneeId: P2 },
    ]);
    expect(diff.moved).toEqual(
      expect.arrayContaining([
        {
          taskId: c.weekly,
          taskName: 'Badkamer schoonmaken',
          roomName: 'Badkamer',
          durationMinutes: 30,
          from: { weekIndex: 1, weekday: 1, assigneeId: P1 },
          to: { weekIndex: 1, weekday: 2, assigneeId: P2 },
        },
        {
          taskId: c.twice,
          taskName: 'Wastafel',
          roomName: 'Badkamer',
          durationMinutes: 10,
          from: { weekIndex: 0, weekday: 3, assigneeId: P2 },
          to: { weekIndex: 0, weekday: 3, assigneeId: P1 },
        },
      ]),
    );
    expect(diff.moved).toHaveLength(2);
  });

  it('includes minutes per person per week before and after', async () => {
    const c = await setup();
    type WeekMinutes = { users: { userId: string; minutes: number }[] }[];
    const res = await c.call('GET', `/api/cycle-plans/${c.draftId}/diff`);
    const { summary } = res.json<{ summary: { before: WeekMinutes; after: WeekMinutes } }>();
    const P1 = c.p1._id.toHexString();
    const P2 = c.p2._id.toHexString();
    const minutes = (weeks: WeekMinutes, userId: string) => weeks.map((w) => w.users.find((u) => u.userId === userId)!.minutes);
    expect(minutes(summary.before, P1)).toEqual([30, 30, 0, 0]);
    expect(minutes(summary.before, P2)).toEqual([10, 0, 10, 0]);
    expect(minutes(summary.after, P1)).toEqual([40, 0, 60, 0]);
    expect(minutes(summary.after, P2)).toEqual([0, 30, 0, 0]);
  });

  it('returns 404 for an unknown plan and 400 for an unsupported comparison', async () => {
    const c = await setup();
    expect((await c.call('GET', '/api/cycle-plans/0123456789abcdef01234567/diff')).statusCode).toBe(404);
    expect((await c.call('GET', `/api/cycle-plans/${c.draftId}/diff?against=yesterday`)).statusCode).toBe(400);
  });
});

describe('POST /api/cycle-plans/:id/apply-proposal', () => {
  it('activates the draft via the normal flow, audited as ai-apply with source ai and the proposal id', async () => {
    const c = await setup();
    const { result, entries } = await expectAudited(c.t, () => c.call('POST', `/api/cycle-plans/${c.draftId}/apply-proposal`), {
      entity: 'cyclePlan',
      action: 'ai-apply',
      source: 'ai',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(200);
    expect(entries[0]!.actorId).toEqual(c.p1._id);
    expect(entries[0]!.meta).toMatchObject({ proposalId: c.proposalId });
    expect(entries[0]!.before).toEqual({ active: false, draft: true });
    expect(entries[0]!.after).toEqual({ active: true, draft: false });

    const active = await findActivePlan(c.t.db);
    expect(active?._id.toHexString()).toBe(c.draftId);
    expect(active).toMatchObject({ draft: false, source: 'ai', proposalId: c.proposalId });
    expect((await findPlanById(c.t.db, new ObjectId(c.activeId)))?.active).toBe(false);

    // generation ran for the new plan, and it is distinguishable from a manual activation
    expect(await countOccurrences(c.t.db, { planId: new ObjectId(c.draftId) })).toBeGreaterThan(0);
    expect(await c.t.db.collection(COLLECTIONS.auditLog).countDocuments({ entityId: new ObjectId(c.draftId), action: 'activate' })).toBe(0);
  });

  it('refuses plans that are not an open draft', async () => {
    const c = await setup();
    const manual = await c.call('POST', `/api/cycle-plans/${c.activeId}/apply-proposal`);
    expect(manual.statusCode).toBe(409);
    expect(manual.json()).toMatchObject({ code: 'not_a_draft' });

    await c.call('POST', `/api/cycle-plans/${c.draftId}/apply-proposal`);
    expect((await c.call('POST', `/api/cycle-plans/${c.draftId}/apply-proposal`)).statusCode).toBe(409);
    expect((await c.call('POST', '/api/cycle-plans/0123456789abcdef01234567/apply-proposal')).statusCode).toBe(404);
  });
});

describe('POST /api/cycle-plans/:id/discard', () => {
  it('marks the draft discarded and inactive, audited as update', async () => {
    const c = await setup();
    const { result, entries } = await expectAudited(c.t, () => c.call('POST', `/api/cycle-plans/${c.draftId}/discard`), {
      entity: 'cyclePlan',
      action: 'update',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(200);
    expect(entries[0]!.after).toEqual({ active: false, discarded: true });
    expect(entries[0]!.meta).toEqual({ proposalId: c.proposalId });
    expect(await findPlanById(c.t.db, new ObjectId(c.draftId))).toMatchObject({ discarded: true, active: false });
    expect((await findActivePlan(c.t.db))?._id.toHexString()).toBe(c.activeId);

    // a discarded draft can no longer be applied or discarded again
    expect((await c.call('POST', `/api/cycle-plans/${c.draftId}/apply-proposal`)).statusCode).toBe(409);
    expect((await c.call('POST', `/api/cycle-plans/${c.draftId}/discard`)).statusCode).toBe(409);
  });

  it('refuses to discard a manual plan', async () => {
    const c = await setup();
    const res = await c.call('POST', `/api/cycle-plans/${c.activeId}/discard`);
    expect(res.statusCode).toBe(409);
  });
});
