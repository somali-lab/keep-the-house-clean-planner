import {
  DEFAULT_CURRENCY_CODE,
  NO_REWARD_GOALS,
  sameRewardGoals,
  scheduleWithAmounts,
  toDayKey,
  updateSettingsInputSchema,
  type BonusScheduleRow,
  type RewardGoals,
} from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { getSettings, StaleBonusScheduleError, updateSettings } from '../data/settings.ts';
import { intervalKeysInUse } from '../data/tasks.ts';
import { removedIntervalKeys } from '../domain/intervals.ts';
import { HttpError, notFound, parseOrThrow } from '../http/errors.ts';
import { toApi } from '../http/serialize.ts';
import { auditContext, requireAdmin } from '../identity/index.ts';

/**
 * The API always returns the bonus schedule and the conversion from points to currency; a missing
 * list means no bonuses, a missing currency means EUR, a missing factor means 0 and missing goals mean both are
 * automatic (ADR-0012, ADR-0013, ADR-0015).
 */
function withDefaults<T extends { bonusSchedule?: unknown; currencyCode?: string; centsPerPoint?: number; rewardGoals?: RewardGoals }>(settings: T) {
  return {
    ...settings,
    bonusSchedule: settings.bonusSchedule ?? [],
    currencyCode: settings.currencyCode ?? DEFAULT_CURRENCY_CODE,
    centsPerPoint: settings.centsPerPoint ?? 0,
    rewardGoals: settings.rewardGoals ?? NO_REWARD_GOALS,
  };
}

export const settingsRoutes: FastifyPluginAsync = async (app) => {
  app.get('/settings', async () => {
    const settings = await getSettings(app.deps.db);
    if (!settings) throw notFound('settings');
    return toApi(withDefaults(settings));
  });

  app.patch('/settings', { preHandler: requireAdmin }, async (request) => {
    const input = parseOrThrow(updateSettingsInputSchema, request.body);
    const current = await getSettings(app.deps.db);
    if (!current) throw notFound('settings');

    if (input.intervals) {
      const removed = removedIntervalKeys(current.intervals, input.intervals);
      if (removed.length > 0) {
        const inUse = new Set(await intervalKeysInUse(app.deps.db));
        const blocked = removed.filter((key) => inUse.has(key));
        if (blocked.length > 0) {
          throw new HttpError(409, 'interval_in_use', 'Interval is used by tasks', { keys: blocked });
        }
      }
    }

    // The amounts apply from today on: the server turns them into a schedule row, and equal amounts write nothing (ADR-0012).
    const { periodBonuses, ...rest } = input;
    const patch: Parameters<typeof updateSettings>[1] = { ...rest };
    // A conversion equal to the one in force (a missing value is the default) is a no-op: it writes and audits nothing (ADR-0013).
    if (patch.currencyCode === (current.currencyCode ?? DEFAULT_CURRENCY_CODE)) delete patch.currencyCode;
    if (patch.centsPerPoint === (current.centsPerPoint ?? 0)) delete patch.centsPerPoint;
    // The goals of the reward meter equal to the ones in force (a missing value is automatic for both) are a no-op too (ADR-0015).
    if (patch.rewardGoals && sameRewardGoals(patch.rewardGoals, current.rewardGoals ?? NO_REWARD_GOALS)) delete patch.rewardGoals;
    let basedOn: { rows: BonusScheduleRow[] | undefined } | undefined;
    if (periodBonuses) {
      const today = toDayKey(app.deps.clock.now(), current.timezone);
      const schedule = current.bonusSchedule ?? [];
      const next = scheduleWithAmounts(schedule, periodBonuses, today);
      if (JSON.stringify(next) !== JSON.stringify(schedule)) {
        patch.bonusSchedule = next;
        // A compare-and-set on the schedule the new one was computed from: two administrators setting amounts at once cannot overwrite each other.
        basedOn = { rows: current.bonusSchedule };
      }
    }

    let settings;
    try {
      settings = await updateSettings(auditContext(request), patch, basedOn ? { basedOnBonusSchedule: basedOn } : {});
    } catch (err) {
      if (err instanceof StaleBonusScheduleError) {
        throw new HttpError(409, 'bonus_schedule_conflict', 'The bonus amounts were changed by someone else; reload and try again');
      }
      throw err;
    }
    if (!settings) throw notFound('settings');
    return toApi(withDefaults(settings));
  });
};
