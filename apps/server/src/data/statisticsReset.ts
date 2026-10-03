import { randomUUID } from 'node:crypto';
import type { AuditContext } from '../audit/context.ts';
import { record } from '../audit/record.ts';
import { getSettings, SETTINGS_ID } from './settings.ts';
import { COLLECTIONS } from './db.ts';
import { occurrencesCollection } from './occurrences.ts';
import { deleteDerivedPointEntries, deleteRedemptions } from './points.ts';

export interface ResetStatisticsResult {
  /** Occurrences before the boundary. */
  deletedOccurrences: number;
  /** Recorded extra work and one-off tasks removed by a restart from today, because they have no planned state to return to. */
  deletedRecorded: number;
  resetOccurrences: number;
  resetTasks: number;
  deletedPastCycles: number;
  /** Entries of the points ledger that went with the history: executions, bonuses (ADR-0011, ADR-0012) and redemptions (requirements 4.12). */
  removedPointEntries: number;
  /** The redemptions among `removedPointEntries`. */
  removedRedemptions: number;
}

interface ResetStatisticsOptions {
  /** Reset every non-open occurrence and task back to open, not just the ones being deleted. */
  restartFromToday: boolean;
  /** The boundary as a day key; it becomes the bonus floor in the settings (ADR-0012). */
  boundaryKey: string;
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
  // Redemptions are booked, not derived, but they go with the history too: starting over removes all of them, a purge those dated before the boundary (requirements 4.12).
  const ledgerBoundary = options.restartFromToday ? undefined : boundary;
  const removedDerived = await deleteDerivedPointEntries(ctx.db, ledgerBoundary);
  const removedRedemptions = await deleteRedemptions(ctx.db, ledgerBoundary);
  const removedPointEntries = removedDerived + removedRedemptions;
  const deletedPastCycles = await ctx.db.collection(COLLECTIONS.cycles).deleteMany({ index: { $lt: boundaryCycle } });
  // Periods that start before the boundary lost (part of) their history, so they never earn a bonus from what remains.
  // The floor only moves forward: a later purge before an earlier day does not give old periods their bonuses back.
  const bonusFloorBefore = (await getSettings(ctx.db))?.bonusFloor;
  const bonusFloor = bonusFloorBefore !== undefined && bonusFloorBefore > options.boundaryKey ? bonusFloorBefore : options.boundaryKey;
  if (bonusFloor !== bonusFloorBefore) {
    await ctx.db.collection(COLLECTIONS.settings).updateOne({ _id: SETTINGS_ID }, { $set: { bonusFloor, updatedAt: ctx.clock.now() } });
  }

  const result: ResetStatisticsResult = {
    deletedOccurrences: deletedOccurrences.deletedCount,
    deletedRecorded: deletedRecorded.deletedCount,
    resetOccurrences: resetOccurrences.modifiedCount,
    resetTasks: resetTasks.modifiedCount,
    deletedPastCycles: deletedPastCycles.deletedCount,
    removedPointEntries,
    removedRedemptions,
  };
  await record(ctx, {
    entity: 'settings',
    entityId: SETTINGS_ID,
    action: 'reset',
    before: { statistics: 'bestaande uitvoeringsgeschiedenis', ...(bonusFloorBefore === undefined ? {} : { bonusFloor: bonusFloorBefore }) },
    after: { statistics: options.restartFromToday ? 'opnieuw gestart' : 'oude data opgeschoond', bonusFloor },
    meta: { ...result, resetId: randomUUID(), scoped: !options.restartFromToday },
  });
  return result;
}
