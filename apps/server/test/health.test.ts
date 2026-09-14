import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { COLLECTIONS, INDEXES } from '../src/data/db.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;

beforeAll(async () => {
  t = await createTestApp();
});

afterAll(async () => {
  await t.close();
});

describe('GET /api/health', () => {
  it('reports ok when mongo is reachable', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/api/health' });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toEqual({ status: 'ok', mongo: 'ok' });
  });

  it('returns JSON 404 for unknown api routes', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/api/nope' });
    expect(res.statusCode).toBe(404);
    expect(res.json()).toEqual({ code: 'not_found' });
  });
});

describe('ensureIndexes', () => {
  it('creates every expected index', async () => {
    for (const [name, expected] of Object.entries(INDEXES)) {
      const indexes = await t.db.collection(name).indexes();
      for (const spec of expected) {
        const match = indexes.find((i) => JSON.stringify(i.key) === JSON.stringify(spec.key));
        expect(match, `${name} ${JSON.stringify(spec.key)}`).toBeDefined();
        expect(Boolean(match?.unique), `${name} unique`).toBe(Boolean(spec.unique));
      }
    }
  });

  it('includes the idempotency and cycle unique indexes', async () => {
    const occ = await t.db.collection(COLLECTIONS.occurrences).indexes();
    expect(occ.find((i) => i.unique && i.key.cycleId === 1 && i.key.plannedDate === 1)).toBeDefined();
    const cycles = await t.db.collection(COLLECTIONS.cycles).indexes();
    expect(cycles.find((i) => i.unique && i.key.index === 1)).toBeDefined();
  });

  it('is idempotent', async () => {
    const { ensureIndexes } = await import('../src/data/db.ts');
    await expect(ensureIndexes(t.db)).resolves.toBeUndefined();
  });
});
