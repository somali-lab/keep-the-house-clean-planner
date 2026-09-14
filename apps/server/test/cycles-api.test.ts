import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { UserDoc } from '../src/data/users.ts';
import { asProfile, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;
let p1: UserDoc;

beforeAll(async () => {
  t = await createTestApp({ now: '2026-09-14T06:00:00.000Z' });
  [p1] = await seededUsers(t);
});

afterAll(async () => {
  await t.close();
});

describe('GET /api/cycles', () => {
  it('is empty before any generation', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/api/cycles' });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toEqual([]);
  });

  it('lists generated cycles in order with day-key boundaries', async () => {
    expect((await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers: asProfile(p1) })).statusCode).toBe(200);
    const res = await t.app.inject({ method: 'GET', url: '/api/cycles' });
    const cycles = res.json<{ index: number; startDate: string; endDate: string; planId: string | null }[]>();
    expect(cycles.map((c) => [c.index, c.startDate, c.endDate])).toEqual([
      [0, '2026-09-14', '2026-10-11'],
      [1, '2026-10-12', '2026-11-08'],
    ]);
    expect(typeof cycles[0]!.planId).toBe('string');
  });

  it('has no write routes', async () => {
    const res = await t.app.inject({ method: 'POST', url: '/api/cycles', headers: asProfile(p1), payload: {} });
    expect(res.statusCode).toBe(404);
  });
});
