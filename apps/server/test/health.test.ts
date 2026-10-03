import { ObjectId, type Db } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { systemContext } from '../src/audit/context.ts';
import { COLLECTIONS, ensureIndexes, GENERATED_SLOT_INDEX, INDEXES } from '../src/data/db.ts';
import {
  insertAdhocOccurrence,
  insertOccurrencesIdempotent,
  type OccurrenceDoc,
} from '../src/data/occurrences.ts';
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
    expect(
      occ.find(
        (i) =>
          i.unique &&
          i.name === GENERATED_SLOT_INDEX &&
          i.key.cycleId === 1 &&
          i.key.plannedDate === 1 &&
          JSON.stringify(i.partialFilterExpression) === JSON.stringify({ origin: 'generated' }),
      ),
    ).toBeDefined();
    expect(occ.find((i) => i.name === 'occurrences_request_id_unique')?.partialFilterExpression).toEqual({
      requestId: { $type: 'string' },
    });
    const cycles = await t.db.collection(COLLECTIONS.cycles).indexes();
    expect(cycles.find((i) => i.unique && i.key.index === 1)).toBeDefined();
  });

  it('is idempotent', async () => {
    await expect(ensureIndexes(t.db)).resolves.toBeUndefined();
  });

  describe('dropping the legacy slot index while another startup does the same', () => {
    /** A database whose legacy index is listed, but whose drop fails like a concurrent startup beat us to it. */
    const fakeDb = (dropError: unknown) =>
      ({
        listCollections: () => ({ toArray: async () => [{ name: 'any' }] }),
        collection: () => ({
          indexes: async () => [{ name: 'cycleId_1_taskId_1_plannedDate_1', key: { cycleId: 1, taskId: 1, plannedDate: 1 } }],
          dropIndex: async () => {
            throw dropError;
          },
          createIndexes: async () => [],
        }),
      }) as unknown as Db;

    it('ignores IndexNotFound (27), because the index is already gone', async () => {
      await expect(ensureIndexes(fakeDb(Object.assign(new Error('index not found'), { code: 27 })))).resolves.toBeUndefined();
    });

    it('still fails on any other error', async () => {
      await expect(ensureIndexes(fakeDb(Object.assign(new Error('not authorized'), { code: 13 })))).rejects.toThrow('not authorized');
    });
  });

  it('migrates the legacy slot index to the partial index and changes nothing on a second run', async () => {
    const db = t.client.db(`${t.dbName}_legacy`);
    try {
      const occurrences = db.collection(COLLECTIONS.occurrences);
      await db.createCollection(COLLECTIONS.occurrences);
      await occurrences.createIndex({ cycleId: 1, taskId: 1, plannedDate: 1 }, { unique: true });
      const ctx = systemContext({ db, clock: t.clock }, t.app.log);
      const now = t.clock.now();
      const cycleId = new ObjectId();
      const taskId = new ObjectId();
      const plannedDate = new Date('2026-09-16T22:00:00Z');
      const doc = (origin: OccurrenceDoc['origin']): OccurrenceDoc => ({
        _id: new ObjectId(),
        taskId,
        cycleId,
        planId: null,
        date: plannedDate,
        plannedDate,
        assigneeId: null,
        status: 'open',
        statusBeforeCompletion: null,
        completedAt: null,
        completedBy: null,
        skipReason: null,
        durationMinutesSnapshot: 10,
        taskNameSnapshot: 'Legacy',
        roomIdSnapshot: null,
        roomNameSnapshot: null,
        origin,
        createdAt: now,
        updatedAt: now,
      });
      const generated = doc('generated');
      await insertOccurrencesIdempotent(ctx, [generated], { setup: true });
      // The legacy index still rejects an ad-hoc occurrence on the same slot.
      expect(await insertAdhocOccurrence(ctx, doc('adhoc'), { setup: true })).toEqual({ inserted: false });

      await ensureIndexes(db);
      const names = async () => (await occurrences.indexes()).map((i) => i.name).sort();
      const migrated = await names();
      expect(migrated).toContain(GENERATED_SLOT_INDEX);
      expect(migrated).not.toContain('cycleId_1_taskId_1_plannedDate_1');
      const slotIndexes = (await occurrences.indexes()).filter(
        (i) => JSON.stringify(i.key) === JSON.stringify({ cycleId: 1, taskId: 1, plannedDate: 1 }),
      );
      expect(slotIndexes).toHaveLength(1);
      expect(slotIndexes[0]).toMatchObject({
        unique: true,
        partialFilterExpression: { origin: 'generated' },
      });

      // Partial filter: ad-hoc occurrences may share the slot key, generated ones still may not.
      expect(await insertAdhocOccurrence(ctx, doc('adhoc'), { setup: true })).toMatchObject({ inserted: true });
      expect(await insertAdhocOccurrence(ctx, doc('adhoc'), { setup: true })).toMatchObject({ inserted: true });
      expect(await insertOccurrencesIdempotent(ctx, [doc('generated')], { setup: true })).toEqual([]);
      expect(await occurrences.countDocuments({ _id: generated._id })).toBe(1);
      expect(await occurrences.countDocuments({})).toBe(3);

      await ensureIndexes(db);
      expect(await names()).toEqual(migrated);
    } finally {
      await db.dropDatabase();
    }
  });

  it('rejects ad-hoc documents in the generated-only bulk insert', async () => {
    const ctx = t.systemCtx();
    const now = t.clock.now();
    await expect(
      insertOccurrencesIdempotent(
        ctx,
        [
          {
            _id: new ObjectId(),
            taskId: new ObjectId(),
            cycleId: new ObjectId(),
            planId: null,
            date: now,
            plannedDate: now,
            assigneeId: null,
            status: 'open',
            statusBeforeCompletion: null,
            completedAt: null,
            completedBy: null,
            skipReason: null,
            durationMinutesSnapshot: 10,
            taskNameSnapshot: 'Guard',
            origin: 'adhoc',
            createdAt: now,
            updatedAt: now,
          },
        ],
        {},
      ),
    ).rejects.toThrow('generated occurrences only');
  });
});
