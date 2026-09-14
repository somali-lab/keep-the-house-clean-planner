import type { SlotDoc } from '../data/cyclePlans.ts';

export interface DiffPosition {
  weekIndex: number;
  /** 0=Sunday..6=Saturday */
  weekday: number;
  assigneeId: string | null;
}

export interface DiffSlot extends DiffPosition {
  taskId: string;
  taskName: string;
  roomName: string | null;
  durationMinutes: number;
}

export interface MovedSlot {
  taskId: string;
  taskName: string;
  roomName: string | null;
  durationMinutes: number;
  from: DiffPosition;
  to: DiffPosition;
}

export interface PlanDiff {
  added: DiffSlot[];
  removed: DiffSlot[];
  moved: MovedSlot[];
  unchanged: number;
}

interface TaskInfo {
  name: string;
  roomName: string | null;
  durationMinutes: number;
}

const mondayFirst = (weekday: number) => (weekday + 6) % 7;
const position = (s: SlotDoc): DiffPosition => ({
  weekIndex: s.weekIndex,
  weekday: s.weekday,
  assigneeId: s.assigneeId?.toHexString() ?? null,
});
const byPosition = (a: SlotDoc, b: SlotDoc) =>
  a.weekIndex - b.weekIndex || mondayFirst(a.weekday) - mondayFirst(b.weekday);
const sameDay = (a: SlotDoc, b: SlotDoc) => a.weekIndex === b.weekIndex && a.weekday === b.weekday;
const sameAssignee = (a: SlotDoc, b: SlotDoc) =>
  a.assigneeId === null || b.assigneeId === null ? a.assigneeId === b.assigneeId : a.assigneeId.equals(b.assigneeId);

/**
 * Compares two plans per task. Identical slots are unchanged; a slot on the same
 * day with another assignee, or any remaining slot that can be paired in cycle
 * order, counts as moved; what is left is added (only in `after`) or removed
 * (only in `before`).
 */
export function diffPlans(before: SlotDoc[], after: SlotDoc[], tasks: Map<string, TaskInfo>): PlanDiff {
  const diff: PlanDiff = { added: [], removed: [], moved: [], unchanged: 0 };
  const taskIds = [...new Set([...before, ...after].map((s) => s.taskId.toHexString()))];

  for (const taskId of taskIds) {
    const info = tasks.get(taskId) ?? { name: taskId, roomName: null, durationMinutes: 0 };
    const toSlot = (s: SlotDoc): DiffSlot => ({
      taskId,
      taskName: info.name,
      roomName: info.roomName,
      durationMinutes: info.durationMinutes,
      ...position(s),
    });
    const move = (from: SlotDoc, to: SlotDoc): MovedSlot => ({
      taskId,
      taskName: info.name,
      roomName: info.roomName,
      durationMinutes: info.durationMinutes,
      from: position(from),
      to: position(to),
    });

    const remainingBefore = before.filter((s) => s.taskId.toHexString() === taskId).sort(byPosition);
    let remainingAfter = after.filter((s) => s.taskId.toHexString() === taskId).sort(byPosition);

    // 1. identical slots
    remainingAfter = remainingAfter.filter((slot) => {
      const index = remainingBefore.findIndex((b) => sameDay(b, slot) && sameAssignee(b, slot));
      if (index === -1) return true;
      remainingBefore.splice(index, 1);
      diff.unchanged++;
      return false;
    });

    // 2. same day, other assignee
    remainingAfter = remainingAfter.filter((slot) => {
      const index = remainingBefore.findIndex((b) => sameDay(b, slot));
      if (index === -1) return true;
      diff.moved.push(move(remainingBefore.splice(index, 1)[0]!, slot));
      return false;
    });

    // 3. pair what is left, in cycle order
    while (remainingBefore.length > 0 && remainingAfter.length > 0) {
      diff.moved.push(move(remainingBefore.shift()!, remainingAfter.shift()!));
    }
    diff.added.push(...remainingAfter.map(toSlot));
    diff.removed.push(...remainingBefore.map(toSlot));
  }
  return diff;
}
