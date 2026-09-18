import type { Slot } from '@huishoudplanner/shared';
import { describe, expect, it } from 'vitest';
import { applyDrop, cellId, dragId, parseDragId, parseDropId, POOL_ID } from './editorModel.ts';

const ANNA = { _id: 'u1', name: 'Anna', unavailableWeekdays: [2] }; // not on Tuesday
const BRAM = { _id: 'u2', name: 'Bram', unavailableWeekdays: [] };
const context = {
  tasks: [
    { _id: 't1', name: 'Badkamer', defaultAssigneeId: 'u1' },
    { _id: 't2', name: 'Stofzuigen' },
  ],
  users: [ANNA, BRAM],
};

const slot = (taskId: string, weekIndex: number, weekday: number, assigneeId: string | null): Slot => ({
  taskId,
  weekIndex,
  weekday,
  assigneeId,
  sortOrder: 0,
});

describe('dnd ids', () => {
  it('round-trips cells, pool, tasks and slots', () => {
    expect(parseDropId(cellId(3, 0, 'u1'))).toEqual({ kind: 'cell', weekIndex: 3, weekday: 0, assigneeId: 'u1' });
    expect(parseDropId(cellId(1, 6, null))).toEqual({ kind: 'cell', weekIndex: 1, weekday: 6, assigneeId: null });
    expect(parseDropId(POOL_ID)).toEqual({ kind: 'pool' });
    expect(parseDropId('day:2:5')).toBeNull();
    expect(parseDropId('nonsense')).toBeNull();
    expect(parseDragId(dragId({ kind: 'pool', taskId: 't1' }))).toEqual({ kind: 'pool', taskId: 't1' });
    expect(parseDragId(dragId({ kind: 'slot', index: 12 }))).toEqual({ kind: 'slot', index: 12 });
  });
});

describe('applyDrop', () => {
  it('adds a slot when dropping a pool task on a cell', () => {
    const result = applyDrop([], { kind: 'pool', taskId: 't1' }, { kind: 'cell', weekIndex: 0, weekday: 1, assigneeId: 'u1' }, context);
    expect(result).toEqual({ ok: true, changed: true, slots: [slot('t1', 0, 1, 'u1')] });
  });

  it('rejects a drop on a weekday the assignee is unavailable, with details for the message', () => {
    const before = [slot('t2', 0, 1, 'u2')];
    const result = applyDrop(before, { kind: 'pool', taskId: 't1' }, { kind: 'cell', weekIndex: 1, weekday: 2, assigneeId: 'u1' }, context);
    expect(result).toEqual({
      ok: false,
      rejection: { reason: 'assignee_unavailable', taskName: 'Badkamer', userName: 'Anna', weekday: 2 },
    });
  });

  it('allows the unavailable weekday for another user or for "wie dan ook"', () => {
    const target = { kind: 'cell' as const, weekIndex: 1, weekday: 2 };
    expect(applyDrop([], { kind: 'pool', taskId: 't1' }, { ...target, assigneeId: 'u2' }, context).ok).toBe(true);
    expect(applyDrop([], { kind: 'pool', taskId: 't1' }, { ...target, assigneeId: null }, context).ok).toBe(true);
  });

  it('rejects the same task twice on one day', () => {
    const before = [slot('t1', 2, 4, 'u2')];
    const result = applyDrop(before, { kind: 'pool', taskId: 't1' }, { kind: 'cell', weekIndex: 2, weekday: 4, assigneeId: null }, context);
    expect(result).toMatchObject({ ok: false, rejection: { reason: 'duplicate_task_day', taskName: 'Badkamer' } });
  });

  it('moves a slot between cells, including to another assignee on the same day', () => {
    const before = [slot('t1', 0, 1, 'u1'), slot('t2', 0, 1, 'u1')];
    const sameDay = applyDrop(before, { kind: 'slot', index: 0 }, { kind: 'cell', weekIndex: 0, weekday: 1, assigneeId: 'u2' }, context);
    expect(sameDay).toEqual({ ok: true, changed: true, slots: [slot('t1', 0, 1, 'u2'), slot('t2', 0, 1, 'u1')] });
    const otherWeek = applyDrop(before, { kind: 'slot', index: 1 }, { kind: 'cell', weekIndex: 3, weekday: 0, assigneeId: null }, context);
    expect(otherWeek).toMatchObject({ ok: true, slots: [slot('t1', 0, 1, 'u1'), slot('t2', 3, 0, null)] });
  });

  it('keeps the plan unchanged when moving a slot onto an unavailable day', () => {
    const before = [slot('t1', 0, 1, 'u1')];
    const result = applyDrop(before, { kind: 'slot', index: 0 }, { kind: 'cell', weekIndex: 0, weekday: 2, assigneeId: 'u1' }, context);
    expect(result.ok).toBe(false);
  });

  it('removes a slot dropped on the pool; no-ops otherwise', () => {
    const before = [slot('t1', 0, 1, 'u1'), slot('t2', 1, 1, null)];
    expect(applyDrop(before, { kind: 'slot', index: 0 }, { kind: 'pool' }, context)).toEqual({
      ok: true,
      changed: true,
      slots: [slot('t2', 1, 1, null)],
    });
    expect(applyDrop(before, { kind: 'pool', taskId: 't1' }, { kind: 'pool' }, context)).toMatchObject({ changed: false });
    expect(
      applyDrop(before, { kind: 'slot', index: 1 }, { kind: 'cell', weekIndex: 1, weekday: 1, assigneeId: null }, context),
    ).toMatchObject({ ok: true, changed: false });
  });
});
