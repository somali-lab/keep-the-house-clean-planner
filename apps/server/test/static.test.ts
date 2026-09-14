import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;
let dist: string;

beforeAll(async () => {
  dist = await mkdtemp(join(tmpdir(), 'hhp-dist-'));
  await mkdir(join(dist, 'assets'));
  await writeFile(join(dist, 'index.html'), '<!doctype html><title>Keep the House Clean</title>');
  await writeFile(join(dist, 'assets', 'app.js'), 'console.log(1)');
  t = await createTestApp({ env: { WEB_DIST_DIR: dist } });
});

afterAll(async () => {
  await t.close();
  await rm(dist, { recursive: true, force: true });
});

describe('web assets', () => {
  it('serves index.html at the root', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/' });
    expect(res.statusCode).toBe(200);
    expect(res.body).toContain('Keep the House Clean');
  });

  it('serves static assets', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/assets/app.js' });
    expect(res.statusCode).toBe(200);
  });

  it('falls back to index.html for client routes', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/vandaag' });
    expect(res.statusCode).toBe(200);
    expect(res.headers['content-type']).toContain('text/html');
  });

  it('keeps JSON 404 for unknown api routes and missing assets', async () => {
    const api = await t.app.inject({ method: 'GET', url: '/api/nope' });
    expect(api.statusCode).toBe(404);
    expect(api.json()).toEqual({ code: 'not_found' });
    const asset = await t.app.inject({ method: 'GET', url: '/assets/missing.js' });
    expect(asset.statusCode).toBe(404);
  });
});
