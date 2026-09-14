import type { FastifyInstance } from 'fastify';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { createUser, updateUser, type UserDoc } from '../src/data/users.ts';
import { requireActor } from '../src/identity/index.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;
let active: UserDoc;
let inactive: UserDoc;

function testRoutes(app: FastifyInstance) {
  app.get('/test/whoami', async (request) => ({
    actorId: request.actor?.actorId.toHexString() ?? null,
    source: request.actor?.source ?? null,
  }));
  app.post('/test/write', { preHandler: requireActor }, async (request) => ({
    actorId: request.actor?.actorId.toHexString(),
    source: request.actor?.source,
  }));
}

beforeAll(async () => {
  t = await createTestApp({ configure: testRoutes });
  const budget = { weekday: 60, weekend: 120 };
  active = await createUser(t.systemCtx(), { name: 'A', color: '#000000', unavailableWeekdays: [], dailyBudgetMinutes: budget });
  inactive = await createUser(t.systemCtx(), { name: 'B', color: '#ffffff', unavailableWeekdays: [], dailyBudgetMinutes: budget });
  await updateUser(t.systemCtx(), inactive._id, { active: false });
});

afterAll(async () => {
  await t.close();
});

describe('identity', () => {
  it('resolves an active profile; source ui for the web client', async () => {
    const res = await t.app.inject({
      method: 'POST',
      url: '/test/write',
      headers: { 'x-profile-id': active._id.toHexString(), 'x-client': 'web' },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toEqual({ actorId: active._id.toHexString(), source: 'ui' });
  });

  it('uses source api without the web client header', async () => {
    const res = await t.app.inject({
      method: 'POST',
      url: '/test/write',
      headers: { 'x-profile-id': active._id.toHexString() },
    });
    expect(res.json()).toMatchObject({ source: 'api' });
  });

  it.each([
    ['missing header', {}],
    ['malformed id', { 'x-profile-id': 'nope' }],
    ['unknown user', { 'x-profile-id': '0123456789abcdef01234567' }],
  ])('rejects writes with %s', async (_label, headers) => {
    const res = await t.app.inject({ method: 'POST', url: '/test/write', headers });
    expect(res.statusCode).toBe(400);
    expect(res.json()).toMatchObject({ code: 'profile_required' });
  });

  it('rejects writes from an inactive profile', async () => {
    const res = await t.app.inject({
      method: 'POST',
      url: '/test/write',
      headers: { 'x-profile-id': inactive._id.toHexString() },
    });
    expect(res.statusCode).toBe(400);
    expect(res.json()).toMatchObject({ code: 'profile_required' });
  });

  it('allows reads without a profile', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/test/whoami' });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toEqual({ actorId: null, source: null });
  });
});
