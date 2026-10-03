import type { LightMyRequestResponse } from 'fastify';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan, findPlanById } from '../src/data/cyclePlans.ts';
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

describe('POST /api/cycle-plans/:id/activate for an AI draft', () => {
  it('clears the draft flag, audits it, and keeps the plan a non-draft after another plan is activated', async () => {
    const c = await setup();
    const preview = await c.call('GET', `/api/cycle-plans/${c.draftId}/activation-preview`);
    const { result, entries } = await expectAudited(
      c.t,
      () => c.call('POST', `/api/cycle-plans/${c.draftId}/activate`, { previewToken: preview.json<{ previewToken: string }>().previewToken }),
      { entity: 'cyclePlan', action: 'activate', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(200);
    expect(entries[0]!.entityId).toEqual(new ObjectId(c.draftId));
    expect(entries[0]!.before).toEqual({ active: false, draft: true });
    expect(entries[0]!.after).toEqual({ active: true, draft: false });
    expect(await findPlanById(c.t.db, new ObjectId(c.draftId))).toMatchObject({ active: true, draft: false, source: 'ai' });

    // Activating the original plan again leaves the former AI plan inactive and not a draft.
    const back = await c.call('GET', `/api/cycle-plans/${c.activeId}/activation-preview`);
    const reactivated = await c.call('POST', `/api/cycle-plans/${c.activeId}/activate`, { previewToken: back.json<{ previewToken: string }>().previewToken });
    expect(reactivated.statusCode, reactivated.body).toBe(200);
    expect(await findPlanById(c.t.db, new ObjectId(c.draftId))).toMatchObject({ active: false, draft: false, source: 'ai' });
  });
});
