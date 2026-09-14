import { randomUUID } from 'node:crypto';
import type { AuditContext } from '../audit/context.ts';
import { record } from '../audit/record.ts';
import { SETTINGS_ID } from './settings.ts';
import { COLLECTIONS } from './db.ts';
import { occurrencesCollection } from './occurrences.ts';

export interface ResetStatisticsResult {
  deletedOccurrences: number;
  resetOccurrences: number;
  resetTasks: number;
  deletedPastCycles: number;
}

/** Audited destructive reset; household definitions and plans are never touched. */
export async function resetStatisticsData(
  ctx: AuditContext,
  startOfToday: Date,
  currentCycle: number,
): Promise<ResetStatisticsResult> {
  const deletedOccurrences = await occurrencesCollection(ctx.db).deleteMany({ date: { $lt: startOfToday } });
  const resetOccurrences = await occurrencesCollection(ctx.db).updateMany(
    {
      $or: [
        { status: { $ne: 'open' } },
        { completedAt: { $ne: null } },
        { completedBy: { $ne: null } },
        { skipReason: { $ne: null } },
      ],
    },
    {
      $set: {
        status: 'open',
        statusBeforeCompletion: null,
        completedAt: null,
        completedBy: null,
        skipReason: null,
        updatedAt: ctx.clock.now(),
      },
    },
  );
  const resetTasks = await ctx.db
    .collection(COLLECTIONS.tasks)
    .updateMany({ lastCompletedAt: { $ne: null } }, { $set: { lastCompletedAt: null, updatedAt: ctx.clock.now() } });
  const deletedPastCycles = await ctx.db.collection(COLLECTIONS.cycles).deleteMany({ index: { $lt: currentCycle } });

  const result: ResetStatisticsResult = {
    deletedOccurrences: deletedOccurrences.deletedCount,
    resetOccurrences: resetOccurrences.modifiedCount,
    resetTasks: resetTasks.modifiedCount,
    deletedPastCycles: deletedPastCycles.deletedCount,
  };
  await record(ctx, {
    entity: 'settings',
    entityId: SETTINGS_ID,
    action: 'reset',
    before: { statistics: 'bestaande uitvoeringsgeschiedenis' },
    after: { statistics: 'opnieuw gestart' },
    meta: { ...result, resetId: randomUUID() },
  });
  return result;
}
