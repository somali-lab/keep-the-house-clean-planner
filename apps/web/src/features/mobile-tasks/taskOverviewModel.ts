import type { OccurrenceView, Room, Task } from '@huishoudplanner/shared';
import { weekIndexFor } from '@huishoudplanner/shared/cycle';
import { addDays, daysBetween } from '@huishoudplanner/shared/time';
import { getLocale } from '../../i18n/runtime.ts';

export interface TaskOverviewRow {
  /** Null for a one-off task, which has no task record. */
  taskId: string | null;
  /** Stable unique key of the row; a one-off task has its own `oneoff:<occurrenceId>` key. */
  key: string;
  taskName: string;
  roomId: string | null;
  roomName: string;
  dates: string[];
  periodStart: string;
  cycleWeek: number;
}

/** One row per task, room, sliding block, and cycle week; dates remain ordered. */
export function taskOverviewRows(
  occurrences: OccurrenceView[],
  tasks: Task[],
  rooms: Room[],
  unknownRoom: string,
  periodStart: string,
  cycleAnchorDate: string,
): TaskOverviewRow[] {
  const taskById = new Map(tasks.map((task) => [task._id, task]));
  const roomById = new Map(rooms.map((room) => [room._id, room]));
  const grouped = new Map<string, TaskOverviewRow>();

  for (const occurrence of [...occurrences].sort((a, b) => a.date.localeCompare(b.date))) {
    const task = occurrence.taskId ? taskById.get(occurrence.taskId) : undefined;
    const roomId = occurrence.roomIdSnapshot ?? task?.roomId ?? null;
    const roomName =
      occurrence.roomNameSnapshot ??
      (roomId ? (roomById.get(roomId)?.name ?? unknownRoom) : unknownRoom);
    const block = Math.floor(daysBetween(periodStart, occurrence.date) / 7);
    const blockStart = addDays(periodStart, block * 7);
    const cycleWeek = weekIndexFor(occurrence.date, cycleAnchorDate) + 1;
    // A one-off task never merges with another record: each gets its own row.
    const groupKey = occurrence.taskId === null ? `oneoff:${occurrence._id}` : `${occurrence.taskId}:${roomId ?? ''}:${roomName}:${blockStart}:${cycleWeek}`;
    const row = grouped.get(groupKey) ?? {
      taskId: occurrence.taskId,
      key: groupKey,
      taskName: task?.name ?? occurrence.taskNameSnapshot,
      roomId,
      roomName,
      dates: [],
      periodStart: blockStart,
      cycleWeek,
    };
    if (!row.dates.includes(occurrence.date)) row.dates.push(occurrence.date);
    grouped.set(groupKey, row);
  }

  return [...grouped.values()].sort(
    (a, b) =>
      a.dates[0]!.localeCompare(b.dates[0]!) ||
      a.roomName.localeCompare(b.roomName, getLocale()) ||
      a.taskName.localeCompare(b.taskName, getLocale()),
  );
}

export function compactDate(dayKey: string): string {
  return new Intl.DateTimeFormat(getLocale(), {
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  })
    .format(new Date(`${dayKey}T12:00:00Z`))
    .replaceAll('.', '');
}

export function datedWeekday(dayKey: string): string {
  return new Intl.DateTimeFormat(getLocale(), {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  })
    .format(new Date(`${dayKey}T12:00:00Z`))
    .replaceAll('.', '');
}
