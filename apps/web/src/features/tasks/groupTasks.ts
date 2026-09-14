import type { Room, Task } from '@huishoudplanner/shared';
import { getLocale } from '../../i18n/runtime.ts';

export const UNKNOWN_ROOM_ID = '__unknown__';

export interface RoomGroup {
  roomId: string;
  /** null for tasks whose room no longer exists. */
  room: Room | null;
  tasks: Task[];
}

const byName = (a: { name: string }, b: { name: string }) =>
  a.name.localeCompare(b.name, getLocale());

/**
 * Groups tasks per room in room sort order. Every active room is listed (even
 * without tasks); inactive rooms only when they still hold tasks. Tasks are
 * sorted by name within a room.
 */
export function groupTasksByRoom(tasks: Task[], rooms: Room[]): RoomGroup[] {
  const roomById = new Map(rooms.map((r) => [r._id, r]));
  const tasksByRoom = new Map<string, Task[]>();
  for (const task of tasks) {
    const key = roomById.has(task.roomId) ? task.roomId : UNKNOWN_ROOM_ID;
    tasksByRoom.set(key, [...(tasksByRoom.get(key) ?? []), task]);
  }

  const groups: RoomGroup[] = [...rooms]
    .filter((room) => room.active || tasksByRoom.has(room._id))
    .sort((a, b) => a.sortOrder - b.sortOrder || byName(a, b))
    .map((room) => ({ roomId: room._id, room, tasks: [...(tasksByRoom.get(room._id) ?? [])].sort(byName) }));

  const orphans = tasksByRoom.get(UNKNOWN_ROOM_ID);
  if (orphans) groups.push({ roomId: UNKNOWN_ROOM_ID, room: null, tasks: [...orphans].sort(byName) });
  return groups;
}
