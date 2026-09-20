import type { FastifyBaseLogger } from 'fastify';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SYSTEM_ACTOR_ID } from '../src/audit/context.ts';
import { auditLogCollection, insertAuditEntry } from '../src/data/auditLog.ts';
import { runAuditRetention } from '../src/domain/auditRetention.ts';
import { asProfile, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

const DAY_MS = 24 * 60 * 60 * 1000;
const log = { info: vi.fn(), error: vi.fn() } as unknown as FastifyBaseLogger;

let t: TestApp | undefined;

afterEach(async () => {
  await t?.close();
  t = undefined;
});

async function setup(): Promise<{ t: TestApp; ages: Map<string, number> }> {
  const app = await createTestApp({ seed: false });
  t = app;
  const now = app.clock.now().getTime();
  const ages = new Map<string, number>();
  for (const [label, ageMs] of [
    ['old', 31 * DAY_MS],
    ['exactly-at-cutoff', 30 * DAY_MS],
    ['recent', 29 * DAY_MS],
    ['today', 0],
  ] as const) {
    await insertAuditEntry(app.db, {
      at: new Date(now - ageMs),
      actorId: SYSTEM_ACTOR_ID,
      entity: 'task',
      entityId: new ObjectId(),
      action: 'update',
      before: {},
      after: { label },
      source: 'system',
    });
    ages.set(label, ageMs);
  }
  return { t: app, ages };
}

const remainingLabels = async (app: TestApp) =>
  (await auditLogCollection(app.db).find({}).sort({ at: 1 }).toArray()).map((e) => e.after.label);

describe('audit retention', () => {
  it('deletes only entries older than AUDIT_RETENTION_DAYS', async () => {
    const { t: app } = await setup();
    const result = await runAuditRetention({ db: app.db, clock: app.clock, log, retentionDays: 30 });
    expect(result).toEqual({ status: 'done', cutoff: new Date(app.clock.now().getTime() - 30 * DAY_MS).toISOString(), deleted: 1 });
    expect(await remainingLabels(app)).toEqual(['exactly-at-cutoff', 'recent', 'today']);
  });

  it('does nothing when AUDIT_RETENTION_DAYS is not set', async () => {
    const { t: app } = await setup();
    expect(await runAuditRetention({ db: app.db, clock: app.clock, log, retentionDays: undefined })).toEqual({ status: 'disabled' });
    expect(await remainingLabels(app)).toEqual(['old', 'exactly-at-cutoff', 'recent', 'today']);
  });

  it('can be started manually by a planner', async () => {
    t = await createTestApp({ env: { AUDIT_RETENTION_DAYS: '30' } });
    const [admin] = await seededUsers(t);
    await insertAuditEntry(t.db, {
      at: new Date(t.clock.now().getTime() - 31 * DAY_MS),
      actorId: SYSTEM_ACTOR_ID,
      entity: 'task',
      entityId: new ObjectId(),
      action: 'update',
      before: {},
      after: { label: 'old' },
      source: 'system',
    });

    const response = await t.app.inject({
      method: 'POST',
      url: '/api/jobs/audit-retention',
      headers: asProfile(admin),
    });

    expect(response.statusCode, response.body).toBe(200);
    expect(response.json()).toMatchObject({ status: 'done', deleted: 1 });
  });
});
