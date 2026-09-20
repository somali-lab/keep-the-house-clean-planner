import { EMPTY_AI_PROMPTS, type TaskSuggestion } from '@huishoudplanner/shared';
import type { LightMyRequestResponse } from 'fastify';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import type { RoomDoc } from '../src/data/rooms.ts';
import type { UserDoc } from '../src/data/users.ts';
import { defaultMockResponders } from '../src/domain/ai/mockResponders.ts';
import type { ExplanationPayload, TaskSuggestionPayload } from '../src/domain/ai/prompt.ts';
import { MockProvider, type MockResponders } from '../src/domain/ai/providers/mock.ts';
import { captureWrites } from './helpers/audit.ts';
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
  keuken: RoomDoc;
  post(url: string, payload: Record<string, unknown>): Promise<LightMyRequestResponse>;
}

async function setup(responders: MockResponders | null = defaultMockResponders): Promise<Ctx> {
  const provider = new MockProvider({ responders: responders ?? {} });
  const app = await createTestApp(responders ? { aiProvider: provider } : {});
  t = app;
  const [p1] = await seededUsers(app);
  const keuken = await seededRoom(app, 'Keuken');
  const badkamer = await seededRoom(app, 'Badkamer');
  const create = async (name: string, roomId: string, intervalKey: string, durationMinutes: number) =>
    app.app.inject({ method: 'POST', url: '/api/tasks', headers: asProfile(p1), payload: { name, roomId, intervalKey, durationMinutes } });
  await create('Keuken: aanrecht', keuken._id.toHexString(), '1w', 20);
  await create('Douche', badkamer._id.toHexString(), '1w', 30);
  return {
    t: app,
    provider,
    p1,
    keuken,
    post: (url, payload) => app.app.inject({ method: 'POST', url, headers: asProfile(p1), payload }),
  };
}

describe('POST /api/ai/test', () => {
  it('tests the supplied provider settings with a small structured request and stores nothing', async () => {
    const c = await setup();
    const capture = await captureWrites(c.t, () => c.post('/api/ai/test', { aiProvider: { type: 'mock' } }));
    expect(capture.result.statusCode, capture.result.body).toBe(200);
    expect(capture.result.json()).toEqual({ ok: true });
    expect(capture.writes).toEqual([]);
    expect(c.provider.requests[0]).toMatchObject({ name: 'connection-test', user: 'Return {"ok":true}.' });
  });

  it('validates the supplied timeout before contacting the provider', async () => {
    const c = await setup();
    const response = await c.post('/api/ai/test', {
      aiProvider: { type: 'ollama', model: 'qwen3:8b', timeoutSeconds: 9 },
    });
    expect(response.statusCode).toBe(400);
    expect(c.provider.requests).toHaveLength(0);
  });
});

describe('POST /api/ai/suggest-tasks', () => {
  it('adds the configured task-suggestion prompt to the provider request', async () => {
    const c = await setup();
    await c.t.app.inject({
      method: 'PATCH',
      url: '/api/settings',
      headers: asProfile(c.p1),
      payload: { aiPrompts: { ...EMPTY_AI_PROMPTS, taskSuggestions: 'Noem vooral seizoensgebonden taken.' } },
    });
    await c.post('/api/ai/suggest-tasks', { roomId: c.keuken._id.toHexString() });
    expect(c.provider.requests[0]!.system).toContain('Noem vooral seizoensgebonden taken.');
  });

  it('returns filtered suggestions and stores nothing', async () => {
    const c = await setup();
    const tasksBefore = await c.t.db.collection(COLLECTIONS.tasks).countDocuments();
    const capture = await captureWrites(c.t, () => c.post('/api/ai/suggest-tasks', { roomId: c.keuken._id.toHexString() }));
    expect(capture.result.statusCode, capture.result.body).toBe(200);
    expect(capture.writes).toEqual([]);

    const { suggestions } = capture.result.json<{ suggestions: TaskSuggestion[] }>();
    // The mock also returned an unknown interval key and a name that already exists in the room.
    expect(suggestions).toEqual([
      { name: 'Keuken: plinten afnemen', intervalKey: '4wk', durationMinutes: 15, notes: 'Vochtige doek.' },
      { name: 'Keuken: lampen afstoffen', intervalKey: 'quarter', durationMinutes: 10, notes: '' },
    ]);
    expect(await c.t.db.collection(COLLECTIONS.tasks).countDocuments()).toBe(tasksBefore);
  });

  it('sends the room, its existing tasks, other rooms and the known intervals', async () => {
    const c = await setup();
    await c.post('/api/ai/suggest-tasks', { roomId: c.keuken._id.toHexString() });
    const payload = JSON.parse(c.provider.requests[0]!.user) as TaskSuggestionPayload;
    expect(payload.room).toBe('Keuken');
    expect(payload.existingTasks).toEqual([{ name: 'Keuken: aanrecht', intervalKey: '1w', durationMinutes: 20 }]);
    expect(payload.otherTasks).toEqual([{ room: 'Badkamer', name: 'Douche' }]);
    expect(payload.intervals.map((i) => i.key)).toEqual(['daily', '3w', '2w', '1w', '2wk', '4wk', 'quarter']);
  });

  it('drops unknown intervals, invalid durations, empty names and duplicates (case-insensitive)', async () => {
    const c = await setup({
      'task-suggestions': [
        JSON.stringify({
          suggestions: [
            { name: 'Oven reinigen', intervalKey: '4wk', durationMinutes: 30 },
            { name: 'oven REINIGEN ', intervalKey: '4wk', durationMinutes: 30 },
            { name: 'KEUKEN: AANRECHT', intervalKey: '1w', durationMinutes: 10 },
            { name: 'Koelkast', intervalKey: 'yearly', durationMinutes: 40 },
            { name: 'Vriezer ontdooien', intervalKey: 'quarter', durationMinutes: 0 },
            { name: 'Afzuigkap', intervalKey: '4wk', durationMinutes: 12.5 },
            { name: '   ', intervalKey: '1w', durationMinutes: 5 },
          ],
        }),
      ],
    });
    const res = await c.post('/api/ai/suggest-tasks', { roomId: c.keuken._id.toHexString() });
    expect(res.json<{ suggestions: TaskSuggestion[] }>().suggestions).toEqual([
      { name: 'Oven reinigen', intervalKey: '4wk', durationMinutes: 30, notes: '' },
    ]);
  });

  it('reports unusable answers as 422 and unknown rooms as 404', async () => {
    const c = await setup({ 'task-suggestions': ['geen json'] });
    const bad = await c.post('/api/ai/suggest-tasks', { roomId: c.keuken._id.toHexString() });
    expect(bad.statusCode).toBe(422);
    expect(bad.json()).toMatchObject({ code: 'ai_invalid_response' });
    expect((await c.post('/api/ai/suggest-tasks', { roomId: '0123456789abcdef01234567' })).statusCode).toBe(404);
    expect((await c.post('/api/ai/suggest-tasks', {})).statusCode).toBe(400);
  });
});

describe('POST /api/ai/explain', () => {
  it('adds the configured explanation prompt to the provider request', async () => {
    const c = await setup();
    await c.t.app.inject({
      method: 'PATCH',
      url: '/api/settings',
      headers: asProfile(c.p1),
      payload: { aiPrompts: { ...EMPTY_AI_PROMPTS, planExplanation: 'Leg het uit in heel eenvoudige taal.' } },
    });
    const planId = (await findActivePlan(c.t.db))!._id.toHexString();
    await c.post('/api/ai/explain', { planId });
    expect(c.provider.requests[0]!.system).toContain('Leg het uit in heel eenvoudige taal.');
  });

  it('returns four sentences for a plan and stores nothing', async () => {
    const c = await setup();
    const planId = (await findActivePlan(c.t.db))!._id.toHexString();
    const capture = await captureWrites(c.t, () => c.post('/api/ai/explain', { planId }));
    expect(capture.result.statusCode, capture.result.body).toBe(200);
    expect(capture.writes).toEqual([]);
    const { rationale } = capture.result.json<{ rationale: string[] }>();
    expect(rationale).toHaveLength(4);
    expect(rationale[0]).toBe('Week 1: 0 taken, verdeeld over de week.');

    const payload = JSON.parse(c.provider.requests[0]!.user) as ExplanationPayload;
    expect(payload.planName).toBe('Standaard');
  });

  it('rejects an explanation without exactly four sentences, and unknown plans', async () => {
    const c = await setup({ 'plan-explanation': [JSON.stringify({ rationale: ['een', 'twee', 'drie'] })] });
    const planId = (await findActivePlan(c.t.db))!._id.toHexString();
    const res = await c.post('/api/ai/explain', { planId });
    expect(res.statusCode).toBe(422);
    expect(res.json<{ code: string; errors: string[] }>()).toMatchObject({ code: 'ai_invalid_response' });
    expect((await c.post('/api/ai/explain', { planId: '0123456789abcdef01234567' })).statusCode).toBe(404);
  });
});

describe('AI disabled', () => {
  it('answers 503 for suggestions and explanations', async () => {
    const c = await setup(null);
    const planId = (await findActivePlan(c.t.db))!._id.toHexString();
    expect((await c.post('/api/ai/suggest-tasks', { roomId: c.keuken._id.toHexString() })).statusCode).toBe(503);
    expect((await c.post('/api/ai/explain', { planId })).json()).toMatchObject({ code: 'ai_disabled' });
  });
});
