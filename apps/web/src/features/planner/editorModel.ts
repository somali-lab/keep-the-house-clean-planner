import type { Slot } from '@huishoudplanner/shared';

/** Minimal shapes the editor needs; API objects satisfy them. */
export interface EditorTask {
  _id: string;
  name: string;
  defaultAssigneeId?: string | null;
}

export interface EditorUser {
  _id: string;
  name: string;
  unavailableWeekdays: number[];
}

export type DragSource = { kind: 'pool'; taskId: string } | { kind: 'slot'; index: number };

export type DropTarget =
  | { kind: 'cell'; weekIndex: number; weekday: number; assigneeId: string | null }
  | { kind: 'pool' };

export type DropRejection =
  | { reason: 'assignee_unavailable'; taskName: string; userName: string; weekday: number }
  | { reason: 'duplicate_task_day'; taskName: string; weekIndex: number; weekday: number };

export type DropResult = { ok: true; slots: Slot[]; changed: boolean } | { ok: false; rejection: DropRejection };

/** Stable dnd ids: `cell:<week>:<weekday>:<assignee|any>`, `pool`, `task:<id>`, `slot:<index>`. */
export const POOL_ID = 'pool';
const ANY = 'any';

export function cellId(weekIndex: number, weekday: number, assigneeId: string | null): string {
  return `cell:${weekIndex}:${weekday}:${assigneeId ?? ANY}`;
}

export function parseDropId(id: string): DropTarget | null {
  if (id === POOL_ID) return { kind: 'pool' };
  const match = /^cell:(\d):(\d):(.+)$/.exec(id);
  if (!match) return null;
  return {
    kind: 'cell',
    weekIndex: Number(match[1]),
    weekday: Number(match[2]),
    assigneeId: match[3] === ANY ? null : match[3]!,
  };
}

export function dragId(source: DragSource): string {
  return source.kind === 'pool' ? `task:${source.taskId}` : `slot:${source.index}`;
}

export function parseDragId(id: string): DragSource | null {
  const task = /^task:(.+)$/.exec(id);
  if (task) return { kind: 'pool', taskId: task[1]! };
  const slot = /^slot:(\d+)$/.exec(id);
  if (slot) return { kind: 'slot', index: Number(slot[1]) };
  return null;
}

/**
 * Applies a drag-and-drop. Hard rules are enforced here so a rejected drop
 * never changes the plan: a slot may not land on a weekday its assignee is
 * unavailable, and a task may appear only once per day.
 */
export function applyDrop(
  slots: Slot[],
  source: DragSource,
  target: DropTarget,
  context: { tasks: EditorTask[]; users: EditorUser[] },
): DropResult {
  const moving = source.kind === 'slot' ? slots[source.index] : undefined;
  if (source.kind === 'slot' && !moving) return { ok: true, slots, changed: false };

  if (target.kind === 'pool') {
    if (source.kind === 'pool') return { ok: true, slots, changed: false };
    return { ok: true, slots: slots.filter((_, i) => i !== source.index), changed: true };
  }

  const taskId = source.kind === 'pool' ? source.taskId : moving!.taskId;
  const task = context.tasks.find((candidate) => candidate._id === taskId);
  const taskName = task?.name ?? taskId;
  const targetAssigneeId = target.assigneeId;

  if (
    moving &&
    moving.weekIndex === target.weekIndex &&
    moving.weekday === target.weekday &&
    moving.assigneeId === targetAssigneeId
  ) {
    return { ok: true, slots, changed: false };
  }

  if (targetAssigneeId !== null) {
    const user = context.users.find((u) => u._id === targetAssigneeId);
    if (user?.unavailableWeekdays.includes(target.weekday)) {
      return {
        ok: false,
        rejection: { reason: 'assignee_unavailable', taskName, userName: user.name, weekday: target.weekday },
      };
    }
  }

  const duplicate = slots.some(
    (slot, i) =>
      !(source.kind === 'slot' && i === source.index) &&
      slot.taskId === taskId &&
      slot.weekIndex === target.weekIndex &&
      slot.weekday === target.weekday,
  );
  if (duplicate) {
    return {
      ok: false,
      rejection: { reason: 'duplicate_task_day', taskName, weekIndex: target.weekIndex, weekday: target.weekday },
    };
  }

  const placed: Slot = {
    taskId,
    weekIndex: target.weekIndex,
    weekday: target.weekday,
    assigneeId: targetAssigneeId,
    sortOrder: moving?.sortOrder ?? 0,
  };
  const next = source.kind === 'slot' ? slots.map((slot, i) => (i === source.index ? placed : slot)) : [...slots, placed];
  return { ok: true, slots: next, changed: true };
}
