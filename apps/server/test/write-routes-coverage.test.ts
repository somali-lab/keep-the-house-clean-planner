import type { LightMyRequestResponse } from 'fastify';
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { defaultMockResponders } from '../src/domain/ai/mockResponders.ts';
import { MockProvider } from '../src/domain/ai/providers/mock.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { findOccurrences } from '../src/data/occurrences.ts';
import type { UserDoc } from '../src/data/users.ts';
import { captureWrites, expectAudited, expectWritesAudited, type ExpectedAudit } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { PNG_BASE64 } from './helpers/badgeImages.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

/**
 * Every write route must produce an audit entry. The list below must name
 * every non-GET route the app registers; adding a write route without a
 * scenario here makes the first test fail.
 */

let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;
let activePlanId: string;
const ids: Record<string, string> = {};

type Method = 'POST' | 'PUT' | 'PATCH' | 'DELETE';

function call(method: Method, url: string, payload?: Record<string, unknown>) {
  return t.app.inject({ method, url, headers: asProfile(p1), ...(payload ? { payload } : {}) });
}

async function occurrenceId(filter: Record<string, unknown>): Promise<string> {
  const [occ] = await findOccurrences(t.db, { status: 'open', ...filter });
  if (!occ) throw new Error(`no occurrence for ${JSON.stringify(filter)}`);
  return occ._id.toHexString();
}

interface Scenario {
  route: string;
  audit: ExpectedAudit;
  /** Sets the data up outside the captured request, for routes that only write when there is something to repair. */
  prepare?(): Promise<void>;
  run(): Promise<LightMyRequestResponse>;
}

/** Ordered: later scenarios use data created by earlier ones. */
const SCENARIOS: Scenario[] = [
  {
    route: 'POST /api/users',
    audit: { entity: 'user', action: 'create' },
    run: () => call('POST', '/api/users', { name: 'Logé', color: '#16a34a' }),
  },
  {
    route: 'PATCH /api/users/:id',
    audit: { entity: 'user', action: 'update' },
    run: () => call('PATCH', `/api/users/${p2._id.toHexString()}`, { name: 'Bram' }),
  },
  {
    route: 'PUT /api/users/:id/browser-notifications',
    audit: { entity: 'user', action: 'update' },
    run: () =>
      call('PUT', `/api/users/${p2._id.toHexString()}/browser-notifications`, {
        enabled: true,
        times: ['18:30', '08:00'],
      }),
  },
  {
    route: 'POST /api/rooms',
    audit: { entity: 'room', action: 'create' },
    run: async () => {
      const res = await call('POST', '/api/rooms', { name: 'Zolder' });
      ids.room = res.json<{ _id: string }>()._id;
      return res;
    },
  },
  {
    route: 'PATCH /api/rooms/:id',
    audit: { entity: 'room', action: 'update' },
    run: () => call('PATCH', `/api/rooms/${ids.room}`, { name: 'Vliering' }),
  },
  {
    route: 'DELETE /api/rooms/:id',
    audit: { entity: 'room', action: 'delete' },
    run: () => call('DELETE', `/api/rooms/${ids.deleteRoom}`),
  },
  {
    route: 'PATCH /api/settings',
    audit: { entity: 'settings', action: 'update' },
    // The amounts of the week and cycle bonuses become a schedule row in the same settings update (ADR-0012).
    run: () =>
      call('PATCH', '/api/settings', {
        vacationRanges: [{ from: '2026-12-24', to: '2026-12-31' }],
        periodBonuses: { weekDone: 5, weekOnTime: 3, cycleDone: 20, cycleOnTime: 10 },
      }),
  },
  {
    route: 'POST /api/tasks',
    audit: { entity: 'task', action: 'create' },
    run: async () => {
      const res = await call('POST', '/api/tasks', { name: 'Stoffen', roomId: ids.room, intervalKey: '1w', durationMinutes: 15 });
      ids.task = res.json<{ _id: string }>()._id;
      return res;
    },
  },
  {
    route: 'PATCH /api/tasks/:id',
    audit: { entity: 'task', action: 'update' },
    run: () => call('PATCH', `/api/tasks/${ids.task}`, { durationMinutes: 20 }),
  },
  {
    route: 'DELETE /api/tasks/:id',
    audit: { entity: 'task', action: 'delete' },
    run: () => call('DELETE', `/api/tasks/${ids.deleteTask}`),
  },
  {
    route: 'POST /api/rooms/:id/tasks/bulk',
    audit: { entity: 'task', action: 'assign' },
    run: () => call('POST', `/api/rooms/${ids.room}/tasks/bulk`, { op: 'reassign', defaultAssigneeId: p2._id.toHexString() }),
  },
  {
    route: 'POST /api/cycle-plans',
    audit: { entity: 'cyclePlan', action: 'create' },
    run: async () => {
      const res = await call('POST', '/api/cycle-plans', { name: 'Nieuw' });
      ids.plan = res.json<{ _id: string }>()._id;
      return res;
    },
  },
  {
    route: 'PATCH /api/cycle-plans/:id',
    audit: { entity: 'cyclePlan', action: 'update' },
    run: () => call('PATCH', `/api/cycle-plans/${ids.plan}`, { name: 'Nieuw plan' }),
  },
  {
    route: 'PUT /api/cycle-plans/:id/slots',
    audit: { entity: 'cyclePlan', action: 'update' },
    run: () =>
      call('PUT', `/api/cycle-plans/${activePlanId}/slots`, {
        slots: [
          { taskId: ids.task, weekIndex: 1, weekday: 1, assigneeId: p1._id.toHexString() },
          { taskId: ids.task, weekIndex: 2, weekday: 1, assigneeId: null },
        ],
      }),
  },
  {
    route: 'POST /api/jobs/nightly',
    audit: { entity: 'occurrence', action: 'create' },
    run: () => call('POST', '/api/jobs/nightly'),
  },
  {
    route: 'PATCH /api/occurrences/:id',
    audit: { entity: 'occurrence', action: 'complete' },
    run: async () => {
      ids.completedOccurrence = await occurrenceId({ assigneeId: p1._id });
      return call('PATCH', `/api/occurrences/${ids.completedOccurrence}`, { action: 'complete' });
    },
  },
  {
    route: 'DELETE /api/occurrences/:id',
    audit: { entity: 'occurrence', action: 'delete' },
    run: () => call('DELETE', `/api/occurrences/${ids.completedOccurrence}`),
  },
  {
    route: 'POST /api/occurrences/:id/claim',
    audit: { entity: 'occurrence', action: 'assign' },
    run: async () => call('POST', `/api/occurrences/${await occurrenceId({ assigneeId: null })}/claim`),
  },
  {
    route: 'POST /api/occurrences',
    audit: { entity: 'occurrence', action: 'create' },
    run: () => call('POST', '/api/occurrences', { taskId: ids.task, date: '2026-09-17' }),
  },
  {
    route: 'POST /api/occurrences/one-off',
    audit: { entity: 'occurrence', action: 'create' },
    run: () => call('POST', '/api/occurrences/one-off', { name: 'Eenmalige klus', durationMinutes: 15, date: '2026-09-17' }),
  },
  {
    // Recorded work is created by the scenario above (date today, done) and then undone.
    route: 'POST /api/occurrences/:id/retract',
    audit: { entity: 'occurrence', action: 'delete' },
    run: async () => {
      const recorded = await call('POST', '/api/occurrences', { taskId: ids.task, date: '2026-09-16', done: true });
      return call('POST', `/api/occurrences/${recorded.json<{ _id: string }>()._id}/retract`);
    },
  },
  {
    route: 'POST /api/promote-suggestions/dismiss',
    audit: { entity: 'settings', action: 'update' },
    run: () =>
      call('POST', '/api/promote-suggestions/dismiss', {
        planId: activePlanId,
        taskId: ids.task,
        weekIndex: 1,
        weekday: 1,
        toWeekday: 3,
        toAssigneeId: null,
        lastEvidenceId: '0123456789abcdef01234567',
      }),
  },
  {
    route: 'POST /api/promote-suggestions/apply',
    audit: { entity: 'cyclePlan', action: 'update' },
    run: () =>
      call('POST', '/api/promote-suggestions/apply', {
        planId: activePlanId,
        taskId: ids.task,
        weekIndex: 1,
        weekday: 1,
        toWeekday: 3,
      }),
  },
  {
    route: 'POST /api/ai/propose-plan',
    audit: { entity: 'cyclePlan', action: 'create', source: 'ai' },
    run: async () => {
      const res = await call('POST', '/api/ai/propose-plan', { constraints: 'geen nat werk doordeweeks' });
      ids.draft = res.json<{ planId: string }>().planId;
      return res;
    },
  },
  {
    route: 'POST /api/ai/rebalance',
    audit: { entity: 'cyclePlan', action: 'create', source: 'ai' },
    run: async () => {
      const res = await call('POST', '/api/ai/rebalance', { planId: activePlanId });
      ids.draftToDiscard = res.json<{ planId: string }>().planId;
      return res;
    },
  },
  {
    route: 'POST /api/cycle-plans/:id/discard',
    audit: { entity: 'cyclePlan', action: 'update' },
    run: () => call('POST', `/api/cycle-plans/${ids.draftToDiscard}/discard`),
  },
  {
    route: 'DELETE /api/cycle-plans/:id',
    audit: { entity: 'cyclePlan', action: 'delete' },
    run: () => call('DELETE', `/api/cycle-plans/${ids.draftToDiscard}`),
  },
  {
    route: 'POST /api/cycle-plans/:id/apply-proposal',
    audit: { entity: 'cyclePlan', action: 'ai-apply', source: 'ai' },
    run: () => call('POST', `/api/cycle-plans/${ids.draft}/apply-proposal`),
  },
  {
    route: 'POST /api/cycle-plans/:id/activate',
    audit: { entity: 'cyclePlan', action: 'activate' },
    run: async () => {
      const preview = await t.app.inject({
        method: 'GET',
        url: `/api/cycle-plans/${ids.plan}/activation-preview`,
        headers: asProfile(p1),
      });
      return call('POST', `/api/cycle-plans/${ids.plan}/activate`, {
        previewToken: preview.json<{ previewToken: string }>().previewToken,
      });
    },
  },
  {
    route: 'POST /api/badges',
    audit: { entity: 'badge', action: 'create' },
    run: async () => {
      const res = await call('POST', '/api/badges', {
        name: 'Stoffenfan',
        rule: { type: 'executions', taskIds: [ids.task], threshold: 1 },
        image: { contentType: 'image/png', data: PNG_BASE64 },
      });
      ids.badge = res.json<{ _id: string }>()._id;
      return res;
    },
  },
  {
    route: 'PATCH /api/badges/:id',
    audit: { entity: 'badge', action: 'update' },
    run: () => call('PATCH', `/api/badges/${ids.badge}`, { name: 'Stoffenkampioen', rule: { type: 'executions', taskIds: [ids.task], threshold: 2 } }),
  },
  {
    route: 'POST /api/badges/examples',
    audit: { entity: 'badge', action: 'create' },
    run: () => call('POST', '/api/badges/examples', { language: 'nl' }),
  },
  {
    route: 'DELETE /api/badges/:id',
    audit: { entity: 'badge', action: 'delete' },
    run: () => call('DELETE', `/api/badges/${ids.badge}`),
  },
  {
    // Recorded one-off work gives the administrator points to redeem; the booking itself is audited as a ledger create.
    route: 'POST /api/points/redemptions',
    audit: { entity: 'points', action: 'create', count: 1 },
    prepare: async () => {
      const earned = await call('POST', '/api/occurrences/one-off', { name: 'Extra klus', durationMinutes: 30, date: '2026-09-16', done: true });
      expect(earned.statusCode, earned.body).toBe(201);
    },
    run: async () => {
      const res = await call('POST', '/api/points/redemptions', { points: 2, note: 'Taart' });
      ids.redemption = res.json<{ _id: string }>()._id;
      return res;
    },
  },
  {
    route: 'DELETE /api/points/redemptions/:id',
    audit: { entity: 'points', action: 'delete', count: 1 },
    run: () => call('DELETE', `/api/points/redemptions/${ids.redemption}`),
  },
  {
    route: 'DELETE /api/stats',
    audit: { entity: 'settings', action: 'reset' },
    run: () => call('DELETE', '/api/stats'),
  },
  {
    // Repairs drift: an execution entry without an occurrence and a bonus entry that no set supports are removed, and both removals are audited once, as a summary.
    route: 'POST /api/points/recompute',
    audit: { entity: 'points', action: 'recompute', count: 1 },
    prepare: async () => {
      const now = new Date('2026-09-16T08:00:00.000Z');
      // eslint-disable-next-line no-restricted-syntax -- creates drift (an entry without an occurrence) that only a reconciliation repairs
      await t.db.collection(COLLECTIONS.pointEntries).insertOne({
        _id: new ObjectId(),
        key: `execution:${new ObjectId().toHexString()}`,
        kind: 'execution',
        personId: p1._id,
        amount: 2,
        date: now,
        weekStart: now,
        occurrenceId: null,
        taskId: null,
        titleSnapshot: 'Verdwaald',
        source: 'live',
        createdAt: now,
        updatedAt: now,
      });
      // eslint-disable-next-line no-restricted-syntax -- creates drift (a bonus that no occurrence supports) that only a reconciliation repairs
      await t.db.collection(COLLECTIONS.pointEntries).insertOne({
        _id: new ObjectId(),
        key: `bonus_week_done:${p1._id.toHexString()}:2026-08-31`,
        kind: 'bonus_week_done',
        personId: p1._id,
        amount: 5,
        date: now,
        weekStart: now,
        periodStart: now,
        occurrenceId: null,
        taskId: null,
        titleSnapshot: '',
        source: 'recompute',
        createdAt: now,
        updatedAt: now,
      });
    },
    run: () => call('POST', '/api/points/recompute'),
  },
  {
    // Last: re-imports the current export, so ids stay the same for the checks after it.
    route: 'POST /api/import/json',
    audit: { entity: 'import', action: 'create', count: 1 },
    run: async () => {
      const file = (await t.app.inject({ method: 'GET', url: '/api/export/json' })).json<Record<string, unknown>>();
      return call('POST', '/api/import/json?mode=replace&confirm=true', file);
    },
  },
];

/**
 * POST routes that are deliberately read-only (AI answers that are not stored).
 * Each one is checked to write nothing at all, so a route cannot hide here.
 */
interface ReadOnlyScenario {
  route: string;
  run(): Promise<LightMyRequestResponse>;
}

const READ_ONLY_POSTS: ReadOnlyScenario[] = [
  {
    route: 'POST /api/ai/test',
    run: () => call('POST', '/api/ai/test', { aiProvider: { type: 'mock' } }),
  },
  {
    // Uses a seeded room so it does not depend on the order of the write scenarios.
    route: 'POST /api/ai/suggest-tasks',
    run: async () => call('POST', '/api/ai/suggest-tasks', { roomId: (await seededRoom(t, 'Keuken'))._id.toHexString() }),
  },
  {
    route: 'POST /api/ai/explain',
    run: () => call('POST', '/api/ai/explain', { planId: activePlanId }),
  },
  {
    // Notifications leave the app but never touch the database.
    route: 'POST /api/jobs/morning-notify',
    run: () => call('POST', '/api/jobs/morning-notify'),
  },
];

const READ_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);
// Clearing or pruning the audit log cannot audit itself without making the
// cleaned log non-empty again. Their behavior and profile requirements are tested separately.
const INTENTIONALLY_UNAUDITED_ROUTES = ['DELETE /api/audit', 'POST /api/jobs/audit-retention'];

beforeAll(async () => {
  // The deterministic mock always proposes a valid plan, so the AI routes really write.
  t = await createTestApp({ aiProvider: new MockProvider({ responders: defaultMockResponders }) });
  [p1, p2] = await seededUsers(t);
  activePlanId = (await findActivePlan(t.db))!._id.toHexString();
  const deleteRoom = await call('POST', '/api/rooms', { name: 'Tijdelijke ruimte' });
  ids.deleteRoom = deleteRoom.json<{ _id: string }>()._id;
  const taskRoomId = (await seededRoom(t, 'Keuken'))._id.toHexString();
  const deleteTask = await call('POST', '/api/tasks', {
    name: 'Tijdelijke taak', roomId: taskRoomId, intervalKey: '1w', durationMinutes: 5,
  });
  ids.deleteTask = deleteTask.json<{ _id: string }>()._id;
});

afterAll(async () => {
  await t.close();
});

describe('audit coverage of write routes', () => {
  it('has a scenario for every registered write route, and no stale scenarios', () => {
    const writeRoutes = t.app.routeList
      .filter((r) => !READ_METHODS.has(r.method))
      .map((r) => `${r.method} ${r.url}`)
      .sort();
    expect(writeRoutes.length).toBeGreaterThan(0);
    expect(writeRoutes).toEqual(
      [
        ...SCENARIOS.map((s) => s.route),
        ...READ_ONLY_POSTS.map((s) => s.route),
        ...INTENTIONALLY_UNAUDITED_ROUTES,
      ].sort(),
    );
  });

  for (const scenario of READ_ONLY_POSTS) {
    it(`${scenario.route} writes nothing`, async () => {
      const capture = await captureWrites(t, scenario.run);
      expect(capture.result.statusCode, capture.result.body).toBeLessThan(300);
      expect(capture.writes).toEqual([]);
      expect(capture.auditInserts).toBe(0);
    });
  }

  for (const scenario of SCENARIOS) {
    it(`${scenario.route} writes an audit entry for every write`, async () => {
      await scenario.prepare?.();
      const { result } = await expectAudited(t, () => expectWritesAudited(t, scenario.run), scenario.audit);
      expect(result.result.statusCode, result.result.body).toBeLessThan(300);
      expect(result.writes.length).toBeGreaterThan(0);
    });
  }

  it('attributes route writes to the requesting profile', async () => {
    const entries = await t.db
      .collection('auditLog')
      .find({ source: { $in: ['ui', 'api'] } })
      .toArray();
    expect(entries.length).toBeGreaterThan(0);
    expect(entries.every((e) => (e.actorId as ObjectId).equals(p1._id))).toBe(true);
  });
});
