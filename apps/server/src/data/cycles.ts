import { ObjectId, type Db, MongoServerError } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { diffFields } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { COLLECTIONS } from './db.ts';

export interface CycleDoc {
  _id: ObjectId;
  index: number;
  /** Day keys 'YYYY-MM-DD' (Monday … Sunday). */
  startDate: string;
  endDate: string;
  planId: ObjectId | null;
  generatedAt: Date;
  generationRunId: string;
}

const cyclesCollection = (db: Db) => db.collection<CycleDoc>(COLLECTIONS.cycles);

export function findCycleByIndex(db: Db, index: number): Promise<CycleDoc | null> {
  return cyclesCollection(db).findOne({ index });
}

export function findCycleById(db: Db, id: ObjectId): Promise<CycleDoc | null> {
  return cyclesCollection(db).findOne({ _id: id });
}

export function listCycles(db: Db): Promise<CycleDoc[]> {
  return cyclesCollection(db).find({}).sort({ index: 1 }).toArray();
}

/**
 * Returns the cycle document for an index, creating it (audited, with runId)
 * when missing. An existing cycle is left untouched.
 */
export async function ensureCycle(
  ctx: AuditContext,
  input: { index: number; startDate: string; endDate: string; planId: ObjectId | null; runId: string },
): Promise<CycleDoc> {
  const existing = await findCycleByIndex(ctx.db, input.index);
  if (existing) return existing;

  const doc: CycleDoc = {
    _id: new ObjectId(),
    index: input.index,
    startDate: input.startDate,
    endDate: input.endDate,
    planId: input.planId,
    generatedAt: ctx.clock.now(),
    generationRunId: input.runId,
  };
  try {
    await cyclesCollection(ctx.db).insertOne(doc);
  } catch (err) {
    // A concurrent run created it first (unique index on `index`).
    if (err instanceof MongoServerError && err.code === 11000) {
      const raced = await findCycleByIndex(ctx.db, input.index);
      if (raced) return raced;
    }
    throw err;
  }
  const { after } = diffFields({}, { ...doc }, { ignore: ['_id'] });
  await record(ctx, { entity: 'cycle', entityId: doc._id, action: 'create', after, meta: { runId: input.runId } });
  return doc;
}

/** Points a cycle at the plan it is generated from; audited, no-op when unchanged. */
export async function setCyclePlan(
  ctx: AuditContext,
  cycle: CycleDoc,
  planId: ObjectId,
  runId: string,
): Promise<CycleDoc> {
  if (cycle.planId?.equals(planId)) return cycle;
  const changes = { planId, generatedAt: ctx.clock.now(), generationRunId: runId };
  const after = await cyclesCollection(ctx.db).findOneAndUpdate(
    { _id: cycle._id },
    { $set: changes },
    { returnDocument: 'after' },
  );
  if (!after) return cycle;
  await record(ctx, {
    entity: 'cycle',
    entityId: cycle._id,
    action: 'update',
    before: { planId: cycle.planId },
    after: { planId },
    meta: { runId },
  });
  return after;
}
