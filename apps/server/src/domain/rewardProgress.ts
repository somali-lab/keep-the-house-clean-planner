import {
  addDays,
  automaticGoal,
  cycleOf,
  DEFAULT_CURRENCY_CODE,
  defaultPointsForDuration,
  fromDayKey,
  NO_REWARD_GOALS,
  pointsToCents,
  resolveRewardGoal,
  rewardPercent,
  toDayKey,
  weekOf,
  type GoalOccurrence,
  type PointsProgressQuery,
  type PointsProgressResponse,
} from '@huishoudplanner/shared';
import { ObjectId, type Db } from 'mongodb';
import type { Clock } from '../clock.ts';
import { findPlannedOccurrences } from '../data/occurrences.ts';
import { sumEarnedPoints } from '../data/points.ts';
import { getSettings } from '../data/settings.ts';
import { findTasksByIds } from '../data/tasks.ts';
import { HttpError } from '../http/errors.ts';
import { taskPoints, toBonusOccurrence } from './points.ts';

/**
 * Progress of one person towards the goal of the current week or cycle (ADR-0015). The period is the one of
 * today in the household timezone, built with the shared day-key and cycle helpers. Earned points are the
 * ledger entries of executions and bonuses dated in it, so a redemption never lowers them; the goal is the
 * explicit goal of the settings, else the points of the work planned for the person as its owner.
 */
export async function pointsProgress(db: Db, clock: Clock, query: PointsProgressQuery): Promise<PointsProgressResponse> {
  const settings = await getSettings(db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const timezone = settings.timezone;
  const today = toDayKey(clock.now(), timezone);
  const period = query.period === 'week' ? weekOf(today) : cycleOf(today, settings.cycleAnchorDate);
  const from = fromDayKey(period.start, timezone);
  const to = fromDayKey(addDays(period.end, 1), timezone);
  const personId = new ObjectId(query.personId);

  const earnedPoints = await sumEarnedPoints(db, personId, from, to);

  const goals = settings.rewardGoals ?? NO_REWARD_GOALS;
  const explicit = query.period === 'week' ? goals.weekPoints : goals.cyclePoints;
  // The planned work is only read when the goal is automatic.
  const automatic =
    explicit === null
      ? automaticGoal(await plannedGoalOccurrences(db, timezone, from, to), query.personId, period)
      : { planned: 0, points: 0 };
  const { goalPoints, source } = resolveRewardGoal(explicit, automatic);

  const centsPerPoint = settings.centsPerPoint ?? 0;
  return {
    personId: query.personId,
    period: query.period,
    start: period.start,
    end: period.end,
    earnedPoints,
    goalPoints,
    goalSource: source,
    percent: rewardPercent(earnedPoints, goalPoints),
    currencyCode: settings.currencyCode ?? DEFAULT_CURRENCY_CODE,
    centsPerPoint,
    money:
      centsPerPoint > 0
        ? {
            earned: pointsToCents(earnedPoints, centsPerPoint),
            goal: goalPoints === null ? null : pointsToCents(goalPoints, centsPerPoint),
          }
        : null,
  };
}

/**
 * The work planned in `[from, to)` with its points: the snapshot of work that is done, else what the task is
 * worth now, else the duration rule for work whose task is gone. An occurrence that cannot be read is left out.
 */
async function plannedGoalOccurrences(db: Db, timezone: string, from: Date, to: Date): Promise<GoalOccurrence[]> {
  const docs = await findPlannedOccurrences(db, from, to);
  // Only the tasks of work without a snapshot are read, and each once.
  const needed = new Map<string, ObjectId>();
  for (const doc of docs) if (doc.pointsSnapshot == null && doc.taskId) needed.set(doc.taskId.toHexString(), doc.taskId);
  const tasks = new Map((await findTasksByIds(db, [...needed.values()])).map((task) => [task._id.toHexString(), task]));
  const items: GoalOccurrence[] = [];
  for (const doc of docs) {
    try {
      const task = doc.taskId ? tasks.get(doc.taskId.toHexString()) : undefined;
      const points = doc.pointsSnapshot ?? (task ? taskPoints(task) : defaultPointsForDuration(doc.durationMinutesSnapshot));
      items.push({ ...toBonusOccurrence(doc, timezone), points });
    } catch {
      // Old data can hold anything; one unreadable occurrence must not hide the meter.
    }
  }
  return items;
}
