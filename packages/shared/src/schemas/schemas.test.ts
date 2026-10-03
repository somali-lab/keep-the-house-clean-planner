import { describe, expect, it } from 'vitest';
import {
  DEFAULT_BROWSER_NOTIFICATIONS,
  DEFAULT_INTERVALS,
  activationPreviewItemSchema,
  createOneOffOccurrenceInputSchema,
  browserNotificationsSchema,
  createTaskInputSchema,
  auditActionSchema,
  auditEntitySchema,
  occurrenceSchema,
  patchOccurrenceInputSchema,
  pointsBalancesQuerySchema,
  pointsEntriesQuerySchema,
  updateTaskInputSchema,
  updateSettingsInputSchema,
  userSchema,
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

  it('rejects completing with both a named person and a take over', () => {
    const result = patchOccurrenceInputSchema.safeParse({ action: 'complete', completedBy: ID, takeOver: true });
    expect(result.success).toBe(false);
    expect(result.error?.issues).toEqual([
      expect.objectContaining({ path: ['completedBy'], message: 'completion_choice_conflict' }),
    ]);
    expect(patchOccurrenceInputSchema.safeParse({ action: 'complete', completedBy: ID }).success).toBe(true);
    expect(patchOccurrenceInputSchema.safeParse({ action: 'complete', takeOver: true }).success).toBe(true);
    expect(patchOccurrenceInputSchema.safeParse({ action: 'complete' }).success).toBe(true);
  });

  it('keeps task points optional on create and within 0..100', () => {
    const base = { name: 'Stofzuigen', roomId: ID, intervalKey: '1w', durationMinutes: 20 };
    expect(createTaskInputSchema.parse(base).points).toBeUndefined();
    expect(createTaskInputSchema.parse({ ...base, points: 0 }).points).toBe(0);
    expect(createTaskInputSchema.parse({ ...base, points: 100 }).points).toBe(100);
    for (const points of [-1, 101, 1.5, '3']) {
      expect(createTaskInputSchema.safeParse({ ...base, points }).success).toBe(false);
      expect(updateTaskInputSchema.safeParse({ points }).success).toBe(false);
    }
    expect(updateTaskInputSchema.safeParse({ points: 7 }).success).toBe(true);
  });

  it('knows the points audit entity and the recompute action', () => {
    expect(auditEntitySchema.safeParse('points').success).toBe(true);
    expect(auditActionSchema.safeParse('recompute').success).toBe(true);
  });

  it('validates the points balances query: optional days, never from after to', () => {
    expect(pointsBalancesQuerySchema.safeParse({}).success).toBe(true);
    expect(pointsBalancesQuerySchema.safeParse({ from: '2026-09-14', to: '2026-09-14' }).success).toBe(true);
    const reversed = pointsBalancesQuerySchema.safeParse({ from: '2026-09-15', to: '2026-09-14' });
    expect(reversed.success ? [] : reversed.error.issues.map((i) => [i.path.join('.'), i.message])).toEqual([['from', 'from_after_to']]);
    expect(pointsBalancesQuerySchema.safeParse({ from: '2026-02-30' }).success).toBe(false);
  });

  it('validates the points entries query: person and both days, at most 371 days', () => {
    const personId = '0123456789abcdef01234567';
    expect(pointsEntriesQuerySchema.safeParse({ personId, from: '2026-01-01', to: '2027-01-06' }).success).toBe(true);
    const tooLarge = pointsEntriesQuerySchema.safeParse({ personId, from: '2026-01-01', to: '2027-01-07' });
    expect(tooLarge.success ? [] : tooLarge.error.issues.map((i) => [i.path.join('.'), i.message])).toEqual([['to', 'range_too_large']]);
    expect(pointsEntriesQuerySchema.safeParse({ personId, from: '2026-09-14' }).success).toBe(false);
    expect(pointsEntriesQuerySchema.safeParse({ from: '2026-09-14', to: '2026-09-20' }).success).toBe(false);
  });
});

describe('browser notification moments', () => {
  const parse = (times: string[], enabled = true) => browserNotificationsSchema.safeParse({ enabled, times });

  it('accepts up to six unique HH:mm times', () => {
    expect(parse([]).success).toBe(true);
    expect(parse(['07:30', '12:00', '18:45', '21:00', '22:15', '23:59']).success).toBe(true);
    expect(parse(['07:30', '12:00', '18:45', '21:00', '22:15', '23:59', '00:00']).success).toBe(false);
  });

  it('rejects malformed and duplicate times', () => {
    expect(parse(['24:00']).success).toBe(false);
    expect(parse(['7:30']).success).toBe(false);
    expect(parse(['08:00', '08:00']).error?.issues.map((i) => i.message)).toContain('duplicate_time');
  });

  it('reads a user without stored moments as disabled with no times', () => {
    const user = userSchema.parse({
      _id: ID,
      name: 'Anna',
      color: '#2563eb',
      active: true,
      unavailableWeekdays: [],
      dailyBudgetMinutes: { weekday: 60, weekend: 120 },
      createdAt: '2026-09-14T08:00:00.000Z',
      updatedAt: '2026-09-14T08:00:00.000Z',
    });
    expect(user.browserNotifications).toEqual(DEFAULT_BROWSER_NOTIFICATIONS);
  });
});
