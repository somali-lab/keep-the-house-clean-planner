import { MongoClient } from 'mongodb';
import { describe, expect, it } from 'vitest';
import { buildApp } from '../src/app.ts';
import { fixedClock } from '../src/clock.ts';
import { loadConfig } from '../src/config.ts';

describe('GET /api/health when mongo is down', () => {
  it('returns 503', async () => {
    const mongoUrl = 'mongodb://127.0.0.1:1/unreachable';
    const client = new MongoClient(mongoUrl, { serverSelectionTimeoutMS: 200 });
    const config = loadConfig({ NODE_ENV: 'test', MONGO_URL: mongoUrl, LOG_LEVEL: 'silent' });
    const app = await buildApp({
      db: client.db(),
      config,
      clock: fixedClock('2026-09-16T08:00:00Z'),
      logger: false,
    });
    const res = await app.inject({ method: 'GET', url: '/api/health' });
    expect(res.statusCode).toBe(503);
    expect(res.json()).toEqual({ status: 'error', mongo: 'error' });
    await app.close();
    await client.close();
  });
});
