import type { OccurrenceView, Room, Task } from '@huishoudplanner/shared';
import { getLocale } from '../../i18n/runtime.ts';

export interface TaskOverviewRow {
  taskId: string;
  taskName: string;
  roomId: string | null;
  roomName: string;
  dates: string[];
}

/** One compact row per task, with each occurrence date shown only once. */
export function taskOverviewRows(
  occurrences: OccurrenceView[],
  tasks: Task[],
  rooms: Room[],
  unknownRoom: string,
): TaskOverviewRow[] {
  const taskById = new Map(tasks.map((task) => [task._id, task]));
  const roomById = new Map(rooms.map((room) => [room._id, room]));
  const grouped = new Map<string, TaskOverviewRow>();

  for (const occurrence of [...occurrences].sort((a, b) => a.date.localeCompare(b.date))) {
    const task = taskById.get(occurrence.taskId);
    const roomId = task?.roomId ?? null;
    const roomName = roomId ? (roomById.get(roomId)?.name ?? unknownRoom) : unknownRoom;
    const row = grouped.get(occurrence.taskId) ?? {
      taskId: occurrence.taskId,
      taskName: task?.name ?? occurrence.taskNameSnapshot,
      roomId,
      roomName,
      dates: [],
    };
    if (!row.dates.includes(occurrence.date)) row.dates.push(occurrence.date);
    grouped.set(occurrence.taskId, row);
  }

  return [...grouped.values()].sort(
    (a, b) =>
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
