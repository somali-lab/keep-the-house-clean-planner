import { describe, expect, it } from 'vitest';
import {
  DEFAULT_INTERVALS,
  activationPreviewItemSchema,
  createOneOffOccurrenceInputSchema,
  createTaskInputSchema,
  occurrenceSchema,
  patchOccurrenceInputSchema,
  updateSettingsInputSchema,
  vacationRangeSchema,
} from './index.ts';

const ID = '0123456789abcdef01234567';

describe('schemas', () => {
  it('requires duration on tasks', () => {
    const result = createTaskInputSchema.safeParse({ name: 'Stofzuigen', roomId: ID, intervalKey: '1w' });
    expect(result.success).toBe(false);
    expect(result.error?.issues.map((i) => i.path.join('.'))).toContain('durationMinutes');
  });

  it('applies task defaults', () => {
    const task = createTaskInputSchema.parse({
      name: 'Stofzuigen',
      roomId: ID,
      intervalKey: '1w',
      durationMinutes: 20,
    });
    expect(task).toMatchObject({ defaultAssigneeId: null, notes: '', tags: [] });
  });

  it('rejects anchors that are not Mondays', () => {
    expect(updateSettingsInputSchema.safeParse({ cycleAnchorDate: '2026-09-14' }).success).toBe(true);
    expect(updateSettingsInputSchema.safeParse({ cycleAnchorDate: '2026-09-15' }).success).toBe(false);
  });

  it('rejects duplicate interval keys and inverted vacation ranges', () => {
    const dup = [...DEFAULT_INTERVALS, DEFAULT_INTERVALS[0]];
    expect(updateSettingsInputSchema.safeParse({ intervals: dup }).success).toBe(false);
    expect(vacationRangeSchema.safeParse({ from: '2026-09-20', to: '2026-09-14' }).success).toBe(false);
  });

  it('includes the three-times-weekly interval in the defaults', () => {
    expect(DEFAULT_INTERVALS).toContainEqual({ key: '3w', label: '3x per week', perCycle: 12, periodDays: 2 });
  });

  it('discriminates occurrence patch actions', () => {
    expect(patchOccurrenceInputSchema.parse({ action: 'skip', reason: 'ziek' })).toEqual({
      action: 'skip',
      reason: 'ziek',
    });
    expect(patchOccurrenceInputSchema.safeParse({ action: 'reschedule' }).success).toBe(false);
    expect(patchOccurrenceInputSchema.safeParse({ action: 'explode' }).success).toBe(false);
    expect(patchOccurrenceInputSchema.parse({ action: 'complete', takeOver: true })).toEqual({
      action: 'complete',
      takeOver: true,
    });
    expect(patchOccurrenceInputSchema.safeParse({ action: 'complete', takeOver: false }).success).toBe(false);
  });
});

describe('one-off task schemas', () => {
  const base = { name: 'Gordijnen ophangen', durationMinutes: 40, date: '2026-09-19' };

  it('trims the name and accepts every optional field', () => {
    expect(createOneOffOccurrenceInputSchema.parse({ ...base, name: '  Gordijnen ophangen  ' })).toEqual(base);
    expect(
      createOneOffOccurrenceInputSchema.parse({
        ...base,
        roomId: null,
        assigneeId: ID,
        done: true,
        requestId: 'one-off-request-key-0001',
      }),
    ).toMatchObject({ roomId: null, assigneeId: ID, done: true, requestId: 'one-off-request-key-0001' });
  });

  it('enforces the name, duration, date and request key bounds', () => {
    const invalid = [
      { ...base, name: '   ' },
      { ...base, name: 'x'.repeat(121) },
      { ...base, durationMinutes: 0 },
      { ...base, durationMinutes: 1.5 },
      { ...base, date: '19-09-2026' },
      { ...base, roomId: 'nope' },
      { ...base, requestId: 'short' },
      { durationMinutes: 40, date: '2026-09-19' },
    ];
    for (const input of invalid) expect(createOneOffOccurrenceInputSchema.safeParse(input).success, JSON.stringify(input)).toBe(false);
    expect(createOneOffOccurrenceInputSchema.safeParse({ ...base, name: 'x'.repeat(120) }).success).toBe(true);
  });

  it('allows a null taskId on occurrences and activation preview items', () => {
    const item = { occurrenceId: ID, cycleIndex: 0, taskId: null, taskName: 'Gordijnen ophangen', date: '2026-09-19', assigneeId: null };
    expect(activationPreviewItemSchema.safeParse(item).success).toBe(true);
    expect(activationPreviewItemSchema.safeParse({ ...item, taskId: undefined }).success).toBe(false);

    const occurrence = {
      _id: ID,
      taskId: null,
      cycleId: ID,
      planId: null,
      date: '2026-09-19',
      plannedDate: '2026-09-19',
      assigneeId: null,
      status: 'open',
      statusBeforeCompletion: null,
      completedAt: null,
      completedBy: null,
      skipReason: null,
      durationMinutesSnapshot: 40,
      taskNameSnapshot: 'Gordijnen ophangen',
      roomIdSnapshot: null,
      roomNameSnapshot: null,
      origin: 'adhoc',
      createdAt: '2026-09-16T08:00:00.000Z',
      updatedAt: '2026-09-16T08:00:00.000Z',
    };
    expect(occurrenceSchema.safeParse(occurrence).success).toBe(true);
    expect(occurrenceSchema.safeParse({ ...occurrence, taskId: ID }).success).toBe(true);
  });
});
