import { EMPTY_AI_PROMPTS, type AiProposalResponse } from '@huishoudplanner/shared';
import type { LightMyRequestResponse } from 'fastify';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan, findPlanById, listPlans } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import type { UserDoc } from '../src/data/users.ts';
import { deterministicPlan } from '../src/domain/ai/mockResponders.ts';
import { getDefaultAiPromptTemplates, type PlanPromptPayload } from '../src/domain/ai/prompt.ts';
import { MockProvider, type MockResponder } from '../src/domain/ai/providers/mock.ts';
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
  provider: MockProvider;
  p1: UserDoc;
  p2: UserDoc;
  weekly: string;
  twice: string;
  post(url: string, payload?: Record<string, unknown>): Promise<LightMyRequestResponse>;
}

const RATIONALE = ['Week 1 uitleg.', 'Week 2 uitleg.', 'Week 3 uitleg.', 'Week 4 uitleg.'];

/** A valid plan built from the prompt the server sent. */
const validAnswer = (request: { user: string }) => JSON.stringify(deterministicPlan(JSON.parse(request.user) as PlanPromptPayload));

async function setup(responder: MockResponder, withProvider = true): Promise<Ctx> {
  const provider = new MockProvider({ responders: { 'plan-proposal': responder } });
  const app = await createTestApp(withProvider ? { aiProvider: provider } : {});
  t = app;
  const [p1, p2] = await seededUsers(app);
  // Persoon 1 cannot do Tuesdays.
  await app.app.inject({ method: 'PATCH', url: `/api/users/${p1._id.toHexString()}`, headers: asProfile(p1), payload: { unavailableWeekdays: [2] } });
  const room = await seededRoom(app, 'Badkamer');
  const task = async (name: string, intervalKey: string, durationMinutes: number) =>
    (
      await app.app.inject({
        method: 'POST',
        url: '/api/tasks',
        headers: asProfile(p1),
        payload: { name, roomId: room._id.toHexString(), intervalKey, durationMinutes },
      })
    ).json<{ _id: string }>()._id;
  const weekly = await task('Badkamer schoonmaken', '1w', 30);
  const twice = await task('Wastafel', '2w', 10);
  return {
    t: app,
    provider,
    p1,
    p2,
    weekly,
    twice,
    post: (url, payload) => app.app.inject({ method: 'POST', url, headers: asProfile(p1), payload: payload ?? {} }),
  };
}

describe('POST /api/ai/propose-plan', () => {
  it('adds the configured proposal prompt to the provider request', async () => {
    const c = await setup((request) => validAnswer(request));
    await c.t.app.inject({
      method: 'PATCH',
      url: '/api/settings',
      headers: asProfile(c.p1),
      payload: { aiPrompts: { ...EMPTY_AI_PROMPTS, planProposal: 'Plan zware taken nooit op opeenvolgende dagen.' } },
    });
    await c.post('/api/ai/propose-plan');
    expect(c.provider.requests[0]!.system).toContain('Plan zware taken nooit op opeenvolgende dagen.');
  });

  it('uses the editable system and user templates and expands their placeholders', async () => {
    const prefix = 'Plan met deze invoer: ';
    const c = await setup((request) => validAnswer({ user: request.user.slice(prefix.length) }));
    const templates = getDefaultAiPromptTemplates();
    templates.planProposal = {
      system: 'Mijn volledige systemprompt. Antwoord volgens {{schema}}',
      user: `${prefix}{{input}}`,
    };
    await c.t.app.inject({
      method: 'PATCH',
      url: '/api/settings',
      headers: asProfile(c.p1),
      payload: { aiPromptTemplates: templates },
    });

    const response = await c.post('/api/ai/propose-plan');
    expect(response.statusCode, response.body).toBe(200);
    expect(c.provider.requests[0]!.system).toContain('Mijn volledige systemprompt. Antwoord volgens {');
    expect(c.provider.requests[0]!.system).not.toContain('{{schema}}');
    expect(c.provider.requests[0]!.user).toMatch(/^Plan met deze invoer: \{"mode":"propose"/);
  });

  it('stores a valid proposal as an inactive draft, audited as AI with the proposal id', async () => {
    const c = await setup((request) => validAnswer(request));
    const activeBefore = await findActivePlan(c.t.db);

    const { result, entries } = await expectAudited(
      c.t,
      () => c.post('/api/ai/propose-plan', { constraints: 'geen nat werk doordeweeks' }),
      { entity: 'cyclePlan', action: 'create', source: 'ai', count: 1 },
    );
    expect(result.statusCode, result.body).toBe(200);
    const body = result.json<AiProposalResponse>();
    expect(body.rationale).toHaveLength(4);
    expect(Array.isArray(body.warnings)).toBe(true);

    const plan = await findPlanById(c.t.db, new ObjectId(body.planId));
    expect(plan).toMatchObject({ active: false, draft: true, source: 'ai', proposalId: body.proposalId, discarded: false });
    expect(plan!.slots).toHaveLength(4 + 8);
    expect(plan!.rationale).toEqual(body.rationale);
    expect(entries[0]!.actorId).toEqual(c.p1._id);
    expect(entries[0]!.meta).toMatchObject({ proposalId: body.proposalId, mode: 'propose' });

    // never auto-activated
    expect((await findActivePlan(c.t.db))?._id).toEqual(activeBefore?._id);
  });

  it('sends tasks, users with availability and budgets, and the constraints', async () => {
    const c = await setup((request) => validAnswer(request));
    await c.post('/api/ai/propose-plan', { constraints: 'zaterdag maximaal een uur per persoon' });
    const [request] = c.provider.requests;
    expect(request!.name).toBe('plan-proposal');
    expect(request!.system).toContain('Never assign a slot to a user on a weekday listed');
    const payload = JSON.parse(request!.user) as PlanPromptPayload;
    expect(payload).toMatchObject({ mode: 'propose', constraints: 'zaterdag maximaal een uur per persoon' });
    expect(payload.tasks.map((task) => [task.name, task.intervalKey, task.perCycle, task.durationMinutes, task.room])).toEqual([
      ['Badkamer schoonmaken', '1w', 4, 30, 'Badkamer'],
      ['Wastafel', '2w', 8, 10, 'Badkamer'],
    ]);
    expect(payload.users[0]).toEqual({
      id: c.p1._id.toHexString(),
      name: 'Persoon 1',
      unavailableWeekdays: [2],
      dailyBudgetMinutes: { weekday: 60, weekend: 120 },
      maxDailyMinutes: { weekday: 60, weekend: 120 },
    });
    expect(payload.currentSlots).toBeUndefined();
    const properties = request!.schema.properties as {
      slots: { items: { properties: Record<string, { enum?: string[] }> } };
    };
    const slotSchema = properties.slots.items.properties;
    expect(slotSchema.taskId!.enum).toEqual([c.weekly, c.twice]);
    expect(slotSchema.assigneeId!.enum).toEqual([c.p1._id.toHexString(), c.p2._id.toHexString()]);
    expect(request!.system).toContain('Never use assigneeId null');
  });

  it('re-prompts once with the error list when the first answer is not valid JSON', async () => {
    const c = await setup((request, attempt) => (attempt === 0 ? 'Hier is je plan: {slots' : validAnswer(request)));
    const res = await c.post('/api/ai/propose-plan');
    expect(res.statusCode, res.body).toBe(200);
    expect(c.provider.requests).toHaveLength(2);
    expect(JSON.parse(c.provider.requests[1]!.user).previousErrors).toEqual(['The answer was not valid JSON.']);
  });

  it('fills every required occurrence when a model returns only a partial plan', async () => {
    const c = await setup((request) => {
      const payload = JSON.parse(request.user) as PlanPromptPayload;
      return JSON.stringify({
        slots: [{ taskId: payload.tasks[0]!.id, weekIndex: 0, weekday: 1, assigneeId: payload.users[0]!.id }],
        rationale: RATIONALE,
      });
    });
    const res = await c.post('/api/ai/propose-plan');
    expect(res.statusCode, res.body).toBe(200);
    const body = res.json<AiProposalResponse>();
    const plan = await findPlanById(c.t.db, new ObjectId(body.planId));
    expect(plan!.slots.filter((slot) => slot.taskId.toHexString() === c.weekly)).toHaveLength(4);
    expect(plan!.slots.filter((slot) => slot.taskId.toHexString() === c.twice)).toHaveLength(8);
    expect(body.warnings.filter((warning) => warning.code === 'interval_mismatch')).toHaveLength(0);
  });

  it('replaces shared assignments with a concrete person who is available that day', async () => {
    const c = await setup((request) => {
      const plan = deterministicPlan(JSON.parse(request.user) as PlanPromptPayload);
      return JSON.stringify({ ...plan, slots: plan.slots.map((slot) => ({ ...slot, assigneeId: null })) });
    });

    const res = await c.post('/api/ai/propose-plan');
    expect(res.statusCode, res.body).toBe(200);
    const saved = await findPlanById(c.t.db, new ObjectId(res.json<AiProposalResponse>().planId));
    expect(saved!.slots.every((slot) => slot.assigneeId !== null)).toBe(true);
    expect(
      saved!.slots.some((slot) => slot.weekday === 2 && slot.assigneeId?.equals(c.p1._id)),
    ).toBe(false);
  });

  it('re-prompts when the first answer breaks a hard rule, naming the violation', async () => {
    const c = await setup((request, attempt) => {
      const plan = deterministicPlan(JSON.parse(request.user) as PlanPromptPayload);
      if (attempt === 0) {
        plan.slots[0] = { ...plan.slots[0]!, weekday: 2, assigneeId: c.p1._id.toHexString() };
      }
      return JSON.stringify(plan);
    });
    const res = await c.post('/api/ai/propose-plan');
    expect(res.statusCode, res.body).toBe(200);
    const retry = JSON.parse(c.provider.requests[1]!.user) as PlanPromptPayload;
    expect(retry.previousErrors?.some((e) => e.startsWith('assignee_unavailable'))).toBe(true);
  });

  it('keeps a usable proposal and reports a warning when a person exceeds the maximum for one day', async () => {
    const c = await setup((request) => {
      const plan = deterministicPlan(JSON.parse(request.user) as PlanPromptPayload);
      const first = plan.slots.find((slot) => slot.taskId === c.weekly)!;
      const other = plan.slots.findIndex((slot) => slot.taskId === c.twice);
      plan.slots[other] = {
        ...plan.slots[other]!,
        weekIndex: first.weekIndex,
        weekday: first.weekday,
        assigneeId: c.p1._id.toHexString(),
      };
      plan.slots[plan.slots.indexOf(first)] = { ...first, assigneeId: c.p1._id.toHexString() };
      return JSON.stringify(plan);
    });
    await c.t.app.inject({
      method: 'PATCH',
      url: `/api/users/${c.p1._id.toHexString()}`,
      headers: asProfile(c.p1),
      payload: { maxDailyMinutes: { weekday: 30, weekend: 30 } },
    });

    const res = await c.post('/api/ai/propose-plan');
    expect(res.statusCode, res.body).toBe(200);
    expect(c.provider.requests).toHaveLength(1);
    expect(res.json<AiProposalResponse>().warnings.some((warning) => warning.code === 'daily_over_budget')).toBe(true);
  });

  it('gives up after the second invalid answer: 422 with errors, nothing stored', async () => {
    const c = await setup(['{"slots":[],"rationale":["only one"]}']);
    const plansBefore = (await listPlans(c.t.db)).length;
    const { result } = await expectAudited(c.t, () => c.post('/api/ai/propose-plan'), {
      entity: 'cyclePlan',
      action: 'create',
      count: 0,
    });
    expect(result.statusCode).toBe(422);
    const body = result.json<{ code: string; errors: string[] }>();
    expect(body.code).toBe('ai_invalid_plan');
    expect(body.errors.length).toBeGreaterThan(0);
    expect(c.provider.requests).toHaveLength(2);
    expect(await listPlans(c.t.db)).toHaveLength(plansBefore);
  });

  it('rejects slots for tasks outside the requested selection', async () => {
    const c = await setup((request) => {
      const payload = JSON.parse(request.user) as PlanPromptPayload;
      return JSON.stringify({
        slots: [{ taskId: c.twice, weekIndex: 0, weekday: 3, assigneeId: null }, ...deterministicPlan(payload).slots],
        rationale: RATIONALE,
      });
    });
    const res = await c.post('/api/ai/propose-plan', { taskIds: [c.weekly] });
    expect(res.statusCode).toBe(422);
    expect(res.json<{ errors: string[] }>().errors.some((e) => e.startsWith('task_not_in_selection'))).toBe(true);
  });

  it('validates the body', async () => {
    const c = await setup((request) => validAnswer(request));
    expect((await c.post('/api/ai/propose-plan', { taskIds: ['0123456789abcdef01234567'] })).statusCode).toBe(400);
    expect((await c.post('/api/ai/propose-plan', { taskIds: [] })).statusCode).toBe(400);
    expect((await c.post('/api/ai/propose-plan', { constraints: 'x'.repeat(2001) })).statusCode).toBe(400);
    expect(c.provider.requests).toHaveLength(0);
  });
});

describe('POST /api/ai/rebalance', () => {
  it('sends the current slots and stores a draft named after the base plan', async () => {
    const c = await setup((request) => validAnswer(request));
    await c.t.app.inject({
      method: 'PATCH',
      url: '/api/settings',
      headers: asProfile(c.p1),
      payload: { aiPrompts: { ...EMPTY_AI_PROMPTS, planRebalance: 'Behoud bestaande dagen als dat mogelijk is.' } },
    });
    const active = (await findActivePlan(c.t.db))!;
    await c.t.app.inject({
      method: 'PUT',
      url: `/api/cycle-plans/${active._id.toHexString()}/slots`,
      headers: asProfile(c.p1),
      payload: { slots: [{ taskId: c.weekly, weekIndex: 0, weekday: 1, assigneeId: c.p1._id.toHexString() }] },
    });
    await c.t.app.inject({
      method: 'PATCH',
      url: `/api/cycle-plans/${active._id.toHexString()}`,
      headers: asProfile(c.p1),
      payload: { weekThemes: ['Keuken', '', '', ''] },
    });

    const res = await c.post('/api/ai/rebalance', { planId: active._id.toHexString(), constraints: 'eerlijker verdelen' });
    expect(res.statusCode, res.body).toBe(200);
    const payload = JSON.parse(c.provider.requests[0]!.user) as PlanPromptPayload;
    expect(payload.mode).toBe('rebalance');
    expect(c.provider.requests[0]!.system).toContain('Behoud bestaande dagen als dat mogelijk is.');
    expect(payload.currentSlots).toEqual([{ taskId: c.weekly, weekIndex: 0, weekday: 1, assigneeId: c.p1._id.toHexString() }]);

    const draft = await findPlanById(c.t.db, new ObjectId(res.json<AiProposalResponse>().planId));
    expect(draft).toMatchObject({ name: 'Standaard (herbalanceerd)', draft: true, active: false, weekThemes: ['Keuken', '', '', ''] });
    const audit = await c.t.db.collection(COLLECTIONS.auditLog).findOne({ entityId: draft!._id, action: 'create' });
    expect(audit?.meta).toMatchObject({ mode: 'rebalance', basePlanId: active._id });
  });

  it('returns 404 for an unknown plan', async () => {
    const c = await setup((request) => validAnswer(request));
    expect((await c.post('/api/ai/rebalance', { planId: '0123456789abcdef01234567' })).statusCode).toBe(404);
  });
});

describe('AI disabled', () => {
  it('answers 503 ai_disabled with the seeded provider and stores nothing', async () => {
    const c = await setup(() => '{}', false);
    const plansBefore = (await listPlans(c.t.db)).length;
    const res = await c.post('/api/ai/propose-plan');
    expect(res.statusCode).toBe(503);
    expect(res.json()).toMatchObject({ code: 'ai_disabled' });
    expect(await listPlans(c.t.db)).toHaveLength(plansBefore);
  });
});
