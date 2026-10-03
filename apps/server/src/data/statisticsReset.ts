import { randomUUID } from 'node:crypto';
import type { AuditContext } from '../audit/context.ts';
import { record } from '../audit/record.ts';
import { SETTINGS_ID } from './settings.ts';
import { COLLECTIONS } from './db.ts';
import { occurrencesCollection } from './occurrences.ts';
import { deleteDerivedPointEntries } from './points.ts';

export interface ResetStatisticsResult {
  /** Occurrences before the boundary. */
  deletedOccurrences: number;
  /** Recorded extra work and one-off tasks removed by a restart from today, because they have no planned state to return to. */
  deletedRecorded: number;
  resetOccurrences: number;
  resetTasks: number;
  deletedPastCycles: number;
  /** Execution and bonus entries of the points ledger that went with the history (ADR-0011, ADR-0012). */
  removedPointEntries: number;
}

interface ResetStatisticsOptions {
  /** Reset every non-open occurrence and task back to open, not just the ones being deleted. */
  restartFromToday: boolean;
}

/** Audited destructive reset; household definitions and plans are never touched. */
export async function resetStatisticsData(
  ctx: AuditContext,
  boundary: Date,
  boundaryCycle: number,
  options: ResetStatisticsOptions,
): Promise<ResetStatisticsResult> {
  const deletedOccurrences = await occurrencesCollection(ctx.db).deleteMany({ date: { $lt: boundary } });
  let resetOccurrences = { modifiedCount: 0 };
  let resetTasks = { modifiedCount: 0 };
  let deletedRecorded = { deletedCount: 0 };
  if (options.restartFromToday) {
    // Recorded work has no planned state to return to, so it is deleted instead of reopened.
    deletedRecorded = await occurrencesCollection(ctx.db).deleteMany({ recordedDone: true });
    resetOccurrences = await occurrencesCollection(ctx.db).updateMany(
      {
        $or: [
          { status: { $ne: 'open' } },
          { completedAt: { $ne: null } },
          { completedBy: { $ne: null } },
          { skipReason: { $ne: null } },
          { pointsSnapshot: { $ne: null } },
        ],
      },
      {
        $set: {
          status: 'open',
          statusBeforeCompletion: null,
          completedAt: null,
          completedBy: null,
          skipReason: null,
          pointsSnapshot: null,
          updatedAt: ctx.clock.now(),
        },
      },
    );
    resetTasks = await ctx.db
      .collection(COLLECTIONS.tasks)
      .updateMany({ lastCompletedAt: { $ne: null } }, { $set: { lastCompletedAt: null, updatedAt: ctx.clock.now() } });
  }
  // The ledger follows the history it is derived from: starting over removes every derived entry (executions and bonuses), a purge those dated before the boundary.
  const removedPointEntries = await deleteDerivedPointEntries(ctx.db, options.restartFromToday ? undefined : boundary);
  const deletedPastCycles = await ctx.db.collection(COLLECTIONS.cycles).deleteMany({ index: { $lt: boundaryCycle } });

  const result: ResetStatisticsResult = {
    deletedOccurrences: deletedOccurrences.deletedCount,
    deletedRecorded: deletedRecorded.deletedCount,
    resetOccurrences: resetOccurrences.modifiedCount,
    resetTasks: resetTasks.modifiedCount,
    deletedPastCycles: deletedPastCycles.deletedCount,
    removedPointEntries,
  };
  await record(ctx, {
    entity: 'settings',
    entityId: SETTINGS_ID,
    action: 'reset',
    before: { statistics: 'bestaande uitvoeringsgeschiedenis' },
    after: { statistics: options.restartFromToday ? 'opnieuw gestart' : 'oude data opgeschoond' },
    meta: { ...result, resetId: randomUUID(), scoped: !options.restartFromToday },
  });
  return result;
}
