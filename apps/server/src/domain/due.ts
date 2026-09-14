import { computeDue, fromDayKey, today, toDayKey, type DueState } from '@huishoudplanner/shared';
import type { Db, ObjectId } from 'mongodb';
import { findOccurrences } from '../data/occurrences.ts';
import { listRooms } from '../data/rooms.ts';
import { getSettings } from '../data/settings.ts';
import { listTasks } from '../data/tasks.ts';
import { HttpError } from '../http/errors.ts';

export interface DueItem {
  taskId: ObjectId;
  taskName: string;
  roomId: ObjectId;
  roomName: string | null;
  intervalKey: string;
  intervalLabel: string;
  periodDays: number;
  daysSince: number;
  ratio: number;
  state: DueState;
  lastCompletedAt: Date | null;
  /** First open occurrence from today on, if the grid still has one planned. */
  nextOccurrence: { id: ObjectId; date: string; assigneeId: ObjectId | null } | null;
}

export interface DueList {
  today: string;
  items: DueItem[];
}

export async function computeDueList(db: Db, now: Date): Promise<DueList> {
  const settings = await getSettings(db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const tz = settings.timezone;
  const todayKey = today(tz, now);

  const [tasks, rooms, upcoming] = await Promise.all([
    listTasks(db, { active: true }),
    listRooms(db),
    findOccurrences(db, { status: 'open', date: { $gte: fromDayKey(todayKey, tz) } }),
  ]);

  const taskById = new Map(tasks.map((t) => [t._id.toHexString(), t]));
  const roomName = new Map(rooms.map((r) => [r._id.toHexString(), r.name]));
  const intervalLabel = new Map(settings.intervals.map((i) => [i.key, i.label]));
  const nextByTask = new Map<string, (typeof upcoming)[number]>();
  for (const occ of upcoming) {
    const key = occ.taskId.toHexString();
    if (!nextByTask.has(key)) nextByTask.set(key, occ);
  }

  const ranked = computeDue(
    tasks.map((t) => ({
      _id: t._id.toHexString(),
      active: t.active,
      intervalKey: t.intervalKey,
      lastCompletedAt: t.lastCompletedAt,
      createdAt: t.createdAt,
    })),
    settings.intervals,
    todayKey,
    tz,
  );

  const items = ranked.map((result): DueItem => {
    const task = taskById.get(result.taskId)!;
    const next = nextByTask.get(result.taskId);
    return {
      taskId: task._id,
      taskName: task.name,
      roomId: task.roomId,
      roomName: roomName.get(task.roomId.toHexString()) ?? null,
      intervalKey: task.intervalKey,
      intervalLabel: intervalLabel.get(task.intervalKey) ?? task.intervalKey,
      periodDays: result.periodDays,
      daysSince: result.daysSince,
      ratio: result.ratio,
      state: result.state,
      lastCompletedAt: task.lastCompletedAt,
      nextOccurrence: next ? { id: next._id, date: toDayKey(next.date, tz), assigneeId: next.assigneeId } : null,
    };
  });

  return { today: todayKey, items };
}

export function summarizeDue(items: Pick<DueItem, 'state'>[]): { due: number; overdue: number } {
  return {
    due: items.filter((i) => i.state === 'due').length,
    overdue: items.filter((i) => i.state === 'overdue').length,
  };
}
