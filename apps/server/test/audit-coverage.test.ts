import { createUserInputSchema } from '@huishoudplanner/shared';
import type { FastifyInstance } from 'fastify';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { SYSTEM_ACTOR_ID } from '../src/audit/context.ts';
import { createUser, updateUser, type UserDoc } from '../src/data/users.ts';
import { parseOrThrow } from '../src/http/errors.ts';
import { auditContext, requireActor } from '../src/identity/index.ts';
import { captureWrites, expectAudited, expectWritesAudited } from './helpers/audit.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;
let actor: UserDoc;

function testRoutes(app: FastifyInstance) {
  app.post('/test/users', { preHandler: requireActor }, async (request) => {
    const input = parseOrThrow(createUserInputSchema, request.body);
    const user = await createUser(auditContext(request), input);
    return { id: user._id.toHexString() };
  });
  app.post('/test/bypass', { preHandler: requireActor }, async () => {
    // eslint-disable-next-line no-restricted-syntax -- deliberately bypasses the data layer to prove the harness catches it
    await app.deps.db.collection('rooms').insertOne({ _id: new ObjectId(), name: 'sneaky' });
    return { ok: true };
  });
}

beforeAll(async () => {
  t = await createTestApp({ configure: testRoutes });
  actor = await createUser(t.systemCtx(), {
    name: 'Persoon 1',
    color: '#2563eb',
    unavailableWeekdays: [],
    dailyBudgetMinutes: { weekday: 60, weekend: 120 },
  });
});

afterAll(async () => {
  await t.close();
});

describe('audit record', () => {
  it('records creates with actor, source and all fields except timestamps', async () => {
    const { result, entries } = await expectAudited(
      t,
      () =>
        t.app.inject({
          method: 'POST',
          url: '/test/users',
          headers: { 'x-profile-id': actor._id.toHexString(), 'x-client': 'web' },
          payload: { name: 'Persoon 2', color: '#db2777' },
        }),
      { entity: 'user', action: 'create', source: 'ui', count: 1 },
    );
    expect(result.statusCode).toBe(200);
    const entry = entries[0]!;
    expect(entry.actorId).toEqual(actor._id);
    expect(entry.entityId.toHexString()).toBe(result.json<{ id: string }>().id);
    expect(entry.at).toEqual(t.clock.now());
    expect(entry.before).toEqual({});
    expect(entry.after).toEqual({
      name: 'Persoon 2',
      color: '#db2777',
      active: true,
      role: 'member',
      unavailableWeekdays: [],
      dailyBudgetMinutes: { weekday: 60, weekend: 120 },
      maxDailyMinutes: { weekday: 60, weekend: 120 },
    });
  });

  it('records updates as changed fields only, as system actor', async () => {
    const { entries } = await expectAudited(
      t,
      () => updateUser(t.systemCtx(), actor._id, { name: 'Anna', dailyBudgetMinutes: { weekday: 90, weekend: 120 } }),
      { entity: 'user', action: 'update', source: 'system', count: 1 },
    );
    expect(entries[0]!.actorId).toEqual(SYSTEM_ACTOR_ID);
    expect(entries[0]!.before).toEqual({ name: 'Persoon 1', dailyBudgetMinutes: { weekday: 60 } });
    expect(entries[0]!.after).toEqual({ name: 'Anna', dailyBudgetMinutes: { weekday: 90 } });
  });

  it('skips write and audit for no-op updates', async () => {
    const capture = await captureWrites(t, () => updateUser(t.systemCtx(), actor._id, { name: 'Anna' }));
    expect(capture.writes).toEqual([]);
    expect(capture.auditInserts).toBe(0);
  });
});

describe('write monitoring harness', () => {
  it('passes when writes go through audited repositories', async () => {
    const capture = await expectWritesAudited(t, () =>
      t.app.inject({
        method: 'POST',
        url: '/test/users',
        headers: { 'x-profile-id': actor._id.toHexString() },
        payload: { name: 'Persoon 3', color: '#16a34a' },
      }),
    );
    expect(capture.writes).toEqual([{ command: 'insert', collection: 'users' }]);
    expect(capture.auditInserts).toBe(1);
  });

  it('detects a write that bypasses the audit helper', async () => {
    const capture = await captureWrites(t, () =>
      t.app.inject({
        method: 'POST',
        url: '/test/bypass',
        headers: { 'x-profile-id': actor._id.toHexString() },
      }),
    );
    expect(capture.result.statusCode).toBe(200);
    expect(capture.unaudited).toBe(true);
  });
});
