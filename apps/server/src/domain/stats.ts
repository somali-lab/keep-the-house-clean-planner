import {
  addDays,
  cycleIndexFor,
  fromDayKey,
  today,
  type CompletionResponse,
  type CompletionRow,
  type IntervalsResponse,
  type StatsGroupBy,
  type UserWorkload,
  type WorkloadResponse,
} from '@huishoudplanner/shared';
import type { Db, Document, ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { listCycles, type CycleDoc } from '../data/cycles.ts';
import { COLLECTIONS } from '../data/db.ts';
import { occurrencesCollection } from '../data/occurrences.ts';
import { listRooms } from '../data/rooms.ts';
import { getSettings, type SettingsDoc } from '../data/settings.ts';
import { resetStatisticsData, type ResetStatisticsResult } from '../data/statisticsReset.ts';
import { listTasks } from '../data/tasks.ts';
import { listUsers } from '../data/users.ts';
import { HttpError } from '../http/errors.ts';

/** Clears execution history while preserving people, rooms, tasks and cycle plans. */
export async function resetStatistics(ctx: AuditContext): Promise<ResetStatisticsResult> {
  const settings = await getSettings(ctx.db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const todayKey = today(settings.timezone, ctx.clock.now());
  const startOfToday = fromDayKey(todayKey, settings.timezone);
  const currentCycle = cycleIndexFor(todayKey, settings.cycleAnchorDate);

  return resetStatisticsData(ctx, startOfToday, currentCycle);
}

/**
 * Statistics are computed from occurrences and their snapshots, so later
 * changes to a task (duration, name) never rewrite history.
 */

interface Scope {
  settings: SettingsDoc;
  cycles: CycleDoc[];
}

/** The last N cycles up to and including the current one, oldest first. */
async function scope(db: Db, now: Date, count: number): Promise<Scope> {
  const settings = await getSettings(db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const current = cycleIndexFor(today(settings.timezone, now), settings.cycleAnchorDate);
  const cycles = (await listCycles(db))
    .filter((c) => c.index <= current)
    .sort((a, b) => b.index - a.index)
    .slice(0, count)
    .reverse();
  return { settings, cycles };
}

const hex = (id: ObjectId | null | undefined) => (id ? id.toHexString() : null);

export async function workloadStats(db: Db, now: Date, count: number): Promise<WorkloadResponse> {
  const { settings, cycles } = await scope(db, now, count);
  if (cycles.length === 0) return { cycles: [] };
  const tz = settings.timezone;

  const [facets] = await occurrencesCollection(db)
    .aggregate<{ planned: GroupRow[]; done: GroupRow[] }>([
      { $match: { cycleId: { $in: cycles.map((c) => c._id) } } },
      { $lookup: { from: COLLECTIONS.cycles, localField: 'cycleId', foreignField: '_id', as: 'cycle' } },
      { $set: { cycle: { $first: '$cycle' } } },
      {
        $set: {
          weekIndex: {
            $floor: {
              $divide: [
                {
                  $dateDiff: {
                    startDate: { $dateFromString: { dateString: '$cycle.startDate', timezone: tz } },
                    endDate: '$date',
                    unit: 'day',
                    timezone: tz,
                  },
                },
                7,
              ],
            },
          },
        },
      },
      {
        $facet: {
          planned: [
            {
              $group: {
                _id: { cycleId: '$cycleId', weekIndex: '$weekIndex', userId: '$assigneeId' },
                minutes: { $sum: '$durationMinutesSnapshot' },
              },
            },
          ],
          done: [
            { $match: { status: 'done' } },
            {
              $group: {
                _id: { cycleId: '$cycleId', weekIndex: '$weekIndex', userId: '$completedBy' },
                minutes: { $sum: '$durationMinutesSnapshot' },
              },
            },
          ],
        },
      },
    ])
    .toArray();

  const users = await listUsers(db);
  const userIds = users
    .filter((u) => u.active || [...facets!.planned, ...facets!.done].some((r) => hex(r._id.userId) === u._id.toHexString()))
    .map((u) => u._id.toHexString());

  const sum = (rows: GroupRow[], cycleId: ObjectId, userId: string | null, weekIndex?: number) =>
    rows
      .filter(
        (r) =>
          r._id.cycleId.equals(cycleId) &&
          hex(r._id.userId) === userId &&
          (weekIndex === undefined || r._id.weekIndex === weekIndex),
      )
      .reduce((total, r) => total + r.minutes, 0);

  const usersFor = (cycleId: ObjectId, weekIndex?: number): UserWorkload[] =>
    userIds.map((userId) => ({
      userId,
      plannedMinutes: sum(facets!.planned, cycleId, userId, weekIndex),
      doneMinutes: sum(facets!.done, cycleId, userId, weekIndex),
    }));

  return {
    cycles: cycles.map((cycle) => ({
      index: cycle.index,
      startDate: cycle.startDate,
      endDate: cycle.endDate,
      users: usersFor(cycle._id),
      unassignedPlannedMinutes: sum(facets!.planned, cycle._id, null),
      weeks: [0, 1, 2, 3].map((weekIndex) => ({
        weekIndex,
        startDate: addDays(cycle.startDate, weekIndex * 7),
        users: usersFor(cycle._id, weekIndex),
        unassignedPlannedMinutes: sum(facets!.planned, cycle._id, null, weekIndex),
      })),
    })),
  };
}

interface GroupRow {
  _id: { cycleId: ObjectId; weekIndex: number; userId: ObjectId | null };
  minutes: number;
}

export async function completionStats(db: Db, now: Date, count: number, groupBy: StatsGroupBy): Promise<CompletionResponse> {
  const { settings, cycles } = await scope(db, now, count);
  if (cycles.length === 0) return { groupBy, rows: [] };
  const startOfToday = fromDayKey(today(settings.timezone, now), settings.timezone);

  const groupKey: Record<StatsGroupBy, string> = { task: '$taskId', room: '$roomId', user: '$assigneeId' };
  const pipeline: Document[] = [
    {
      $match: {
        cycleId: { $in: cycles.map((c) => c._id) },
        $or: [{ status: 'done' }, { status: 'skipped' }, { status: 'open', date: { $lt: startOfToday } }],
      },
    },
    ...(groupBy === 'room'
      ? [
          { $lookup: { from: COLLECTIONS.tasks, localField: 'taskId', foreignField: '_id', as: 'task' } },
          { $set: { roomId: { $first: '$task.roomId' } } },
        ]
      : []),
    {
      $group: {
        _id: groupKey[groupBy],
        snapshotName: { $max: '$taskNameSnapshot' },
        done: { $sum: { $cond: [{ $eq: ['$status', 'done'] }, 1, 0] } },
        skipped: { $sum: { $cond: [{ $eq: ['$status', 'skipped'] }, 1, 0] } },
        missed: { $sum: { $cond: [{ $eq: ['$status', 'open'] }, 1, 0] } },
      },
    },
  ];
  const grouped = await occurrencesCollection(db)
    .aggregate<{ _id: ObjectId | null; snapshotName: string; done: number; skipped: number; missed: number }>(pipeline)
    .toArray();

  const [tasks, rooms, users] = await Promise.all([listTasks(db), listRooms(db), listUsers(db)]);
  const names = new Map<string, string>([
    ...tasks.map((t) => [t._id.toHexString(), t.name] as [string, string]),
    ...rooms.map((r) => [r._id.toHexString(), r.name] as [string, string]),
    ...users.map((u) => [u._id.toHexString(), u.name] as [string, string]),
  ]);

  const rows: CompletionRow[] = grouped.map((g) => {
    const key = hex(g._id);
    const total = g.done + g.skipped + g.missed;
    return {
      key,
      name: key === null ? '' : (names.get(key) ?? g.snapshotName),
      done: g.done,
      skipped: g.skipped,
      missed: g.missed,
      rate: total === 0 ? null : g.done / total,
    };
  });
  rows.sort((a, b) => (a.rate ?? 2) - (b.rate ?? 2) || a.name.localeCompare(b.name, 'nl'));
  return { groupBy, rows };
}

export async function intervalStats(db: Db, now: Date, count: number): Promise<IntervalsResponse> {
  const { settings, cycles } = await scope(db, now, count);
  const tz = settings.timezone;

  const gaps =
    cycles.length === 0
      ? []
      : await occurrencesCollection(db)
          .aggregate<{ _id: ObjectId; completions: number; averageDays: number | null }>([
            { $match: { cycleId: { $in: cycles.map((c) => c._id) }, status: 'done', completedAt: { $ne: null } } },
            {
              $setWindowFields: {
                partitionBy: '$taskId',
                sortBy: { completedAt: 1 },
                output: { previous: { $shift: { output: '$completedAt', by: -1 } } },
              },
            },
            {
              $set: {
                gap: {
                  $cond: [
                    { $eq: ['$previous', null] },
                    null,
                    { $dateDiff: { startDate: '$previous', endDate: '$completedAt', unit: 'day', timezone: tz } },
                  ],
                },
              },
            },
            { $group: { _id: '$taskId', completions: { $sum: 1 }, averageDays: { $avg: '$gap' } } },
          ])
          .toArray();

  const byTask = new Map(gaps.map((g) => [g._id.toHexString(), g]));
  const periodByKey = new Map(settings.intervals.map((i) => [i.key, i.periodDays]));
  const tasks = await listTasks(db);

  const rows = tasks
    .filter((t) => t.active || byTask.has(t._id.toHexString()))
    .map((t) => {
      const stats = byTask.get(t._id.toHexString());
      const periodDays = periodByKey.get(t.intervalKey) ?? 0;
      const averageDays = stats?.averageDays ?? null;
      return {
        taskId: t._id.toHexString(),
        name: t.name,
        intervalKey: t.intervalKey,
        periodDays,
        completions: stats?.completions ?? 0,
        averageDays,
        deviation: averageDays === null || periodDays === 0 ? null : averageDays / periodDays,
      };
    });
  rows.sort(
    (a, b) =>
      (b.deviation ?? Number.NEGATIVE_INFINITY) - (a.deviation ?? Number.NEGATIVE_INFINITY) ||
      a.name.localeCompare(b.name, 'nl'),
  );
  return { rows };
}
