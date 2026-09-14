import type { ObjectId } from 'mongodb';

interface SlotLike {
  taskId: ObjectId;
  weekIndex: number;
  weekday: number;
  assigneeId: ObjectId | null;
  sortOrder: number;
}

/** Identity of a slot within a plan; unique because a task may appear only once per day. */
export function slotKey(slot: Pick<SlotLike, 'taskId' | 'weekIndex' | 'weekday'>): string {
  return `${slot.taskId.toHexString()}:${slot.weekIndex}:${slot.weekday}`;
}

const mondayFirst = (weekday: number) => (weekday + 6) % 7;

export function sortSlots<S extends SlotLike>(slots: S[]): S[] {
  return [...slots].sort(
    (a, b) =>
      a.weekIndex - b.weekIndex ||
      mondayFirst(a.weekday) - mondayFirst(b.weekday) ||
      a.sortOrder - b.sortOrder ||
      a.taskId.toHexString().localeCompare(b.taskId.toHexString()),
  );
}

export interface SlotDiff<S> {
  added: S[];
  removed: S[];
  changed: { before: S; after: S }[];
}

function sameSlot(a: SlotLike, b: SlotLike): boolean {
  const assigneeSame =
    a.assigneeId === null || b.assigneeId === null ? a.assigneeId === b.assigneeId : a.assigneeId.equals(b.assigneeId);
  return assigneeSame && a.sortOrder === b.sortOrder;
}

export function diffSlots<S extends SlotLike>(before: S[], after: S[]): SlotDiff<S> {
  const beforeByKey = new Map(before.map((s) => [slotKey(s), s]));
  const afterByKey = new Map(after.map((s) => [slotKey(s), s]));
  const diff: SlotDiff<S> = { added: [], removed: [], changed: [] };
  for (const [key, slot] of afterByKey) {
    const old = beforeByKey.get(key);
    if (!old) diff.added.push(slot);
    else if (!sameSlot(old, slot)) diff.changed.push({ before: old, after: slot });
  }
  for (const [key, slot] of beforeByKey) {
    if (!afterByKey.has(key)) diff.removed.push(slot);
  }
  return diff;
}
