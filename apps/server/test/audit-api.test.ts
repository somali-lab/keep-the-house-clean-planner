import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { SYSTEM_ACTOR_ID } from '../src/audit/context.ts';
import { updateUser, type UserDoc } from '../src/data/users.ts';
import { asProfile, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

interface EntryJson {
  _id: string;
  at: string;
  actorId: string;
  entity: string;
  entityId: string;
  action: string;
  source: string;
  before: Record<string, unknown>;
  after: Record<string, unknown>;
}

interface PageJson {
  items: EntryJson[];
  nextCursor: string | null;
}

let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
let roomId: string;

async function list(query = ''): Promise<PageJson> {
  const res = await t.app.inject({ method: 'GET', url: `/api/audit${query ? `?${query}` : ''}` });
  expect(res.statusCode, res.body).toBe(200);
  return res.json<PageJson>();
}

beforeAll(async () => {
  t = await createTestApp({ now: '2026-09-16T08:00:00.000Z' });
  [p1, p2] = await seededUsers(t);
  // This suite exercises actor/source filtering, so give its second actor admin rights explicitly.
  await updateUser(t.systemCtx(), p2._id, { role: 'admin' });
  t.clock.set('2026-09-17T08:00:00.000Z');
  const room = await t.app.inject({ method: 'POST', url: '/api/rooms', headers: asProfile(p1), payload: { name: 'Zolder' } });
  roomId = room.json<{ _id: string }>()._id;
  t.clock.set('2026-09-18T08:00:00.000Z');
  await t.app.inject({ method: 'PATCH', url: `/api/rooms/${roomId}`, headers: asProfile(p2, 'api'), payload: { name: 'Vliering' } });
  await t.app.inject({ method: 'PATCH', url: `/api/users/${p2._id.toHexString()}`, headers: asProfile(p2), payload: { name: 'Bram' } });
});

afterAll(async () => {
  await t.close();
});

describe('GET /api/audit', () => {
  it('returns entries newest first', async () => {
    const { items } = await list('limit=200');
    const times = items.map((i) => i.at);
    expect(times).toEqual([...times].sort().reverse());
    expect(items[0]).toMatchObject({ entity: 'user', action: 'update', after: { name: 'Bram' } });
  });

  it('filters by entity and entityId (history of one entity)', async () => {
    const { items } = await list(`entity=room&entityId=${roomId}`);
    expect(items.map((i) => i.action)).toEqual(['update', 'create']);
    expect(items[0]!.before).toEqual({ name: 'Zolder' });
  });

  it('filters by actor and source', async () => {
    const byP2 = await list(`actorId=${p2._id.toHexString()}`);
    expect(byP2.items.map((i) => [i.entity, i.source])).toEqual([
      ['user', 'ui'],
      ['room', 'api'],
    ]);
    const system = await list('source=system&limit=200');
    expect(system.items.length).toBeGreaterThan(0);
    expect(system.items.every((i) => i.actorId === SYSTEM_ACTOR_ID.toHexString())).toBe(true);
    const api = await list('source=api');
    expect(api.items.map((i) => i.entity)).toEqual(['room']);
  });

  it('filters by date range (inclusive)', async () => {
    const day17 = await list('from=2026-09-17T00:00:00.000Z&to=2026-09-17T23:59:59.999Z');
    expect(day17.items.map((i) => [i.entity, i.action])).toEqual([['room', 'create']]);
    const exact = await list('from=2026-09-18T08:00:00.000Z&to=2026-09-18T08:00:00.000Z');
    expect(exact.items).toHaveLength(2);
  });

  it('paginates with a stable cursor, even when entries share a timestamp', async () => {
    const all = (await list('limit=200')).items.map((i) => i._id);
    expect(all.length).toBeGreaterThan(5);

    const seen: string[] = [];
    let cursor: string | null = null;
    let pages = 0;
    do {
      const page: PageJson = await list(`limit=3${cursor ? `&cursor=${cursor}` : ''}`);
      expect(page.items.length).toBeLessThanOrEqual(3);
      seen.push(...page.items.map((i) => i._id));
      cursor = page.nextCursor;
      pages++;
    } while (cursor && pages < 50);

    expect(seen).toEqual(all);
    expect(new Set(seen).size).toBe(seen.length);
  });

  it('returns no cursor on the last page', async () => {
    const page = await list(`entity=room&entityId=${roomId}&limit=2`);
    expect(page.items).toHaveLength(2);
    expect(page.nextCursor).toBeNull();
  });

  it('validates query parameters', async () => {
    for (const query of ['entity=banana', 'actorId=nope', 'limit=0', 'limit=500', 'from=yesterday', 'cursor=bm9wZQ']) {
      const res = await t.app.inject({ method: 'GET', url: `/api/audit?${query}` });
      expect(res.statusCode, query).toBe(400);
    }
  });

  it('requires a profile and clears the complete history', async () => {
    expect((await t.app.inject({ method: 'DELETE', url: '/api/audit' })).statusCode).toBe(400);
    const cleared = await t.app.inject({ method: 'DELETE', url: '/api/audit', headers: asProfile(p1) });
    expect(cleared.statusCode).toBe(200);
    expect(cleared.json<{ deleted: number }>().deleted).toBeGreaterThan(0);
    expect((await list('limit=200')).items).toEqual([]);
  });
});
