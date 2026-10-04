import { describe, expect, it } from 'vitest';
import { toInt, toOccurrence } from './occurrence.ts';
import type { components } from './v2/schema';

const RAW: components['schemas']['OccurrenceResponse'] = {
  id: 'o1',
  taskId: null,
  cycleId: 'c1',
  planId: null,
  date: '2026-09-16',
  plannedDate: '2026-09-15',
  assigneeId: null,
  status: 'done',
  statusBeforeCompletion: 'open',
  completedAt: '2026-09-16T08:00:00Z',
  completedBy: 'u1',
  skipReason: null,
  durationMinutesSnapshot: 25,
  taskNameSnapshot: 'Kast ophalen',
  roomIdSnapshot: null,
  roomNameSnapshot: null,
  origin: 'adhoc',
  recordedDone: true,
  requestId: null,
  pointsSnapshot: 25,
  pointsOverride: '12',
  periodOwnerId: null,
  createdAt: '2026-09-16T07:00:00Z',
  updatedAt: '2026-09-16T08:00:00Z',
  isOverdue: false,
  movedFrom: '2026-09-15',
  cycleIndex: 0,
  weekIndex: '1',
};

describe('toOccurrence', () => {
  it('keeps the id as id, and reads whole numbers whether they arrive as numbers or as text', () => {
    const occurrence = toOccurrence(RAW);
    expect(occurrence).toMatchObject({
      id: 'o1',
      durationMinutesSnapshot: 25,
      pointsSnapshot: 25,
      pointsOverride: 12,
      cycleIndex: 0,
      weekIndex: 1,
      taskId: null,
      movedFrom: '2026-09-15',
    });
    expect(occurrence).not.toHaveProperty('_id');
  });

  it('keeps a missing points value as null', () => {
    expect(toOccurrence({ ...RAW, pointsSnapshot: null, pointsOverride: null })).toMatchObject({
      pointsSnapshot: null,
      pointsOverride: null,
    });
  });
});

describe('toInt', () => {
  it('turns the text form of a whole number into a number', () => {
    expect(toInt('7')).toBe(7);
    expect(toInt(7)).toBe(7);
    expect(toInt('-1')).toBe(-1);
  });
});
