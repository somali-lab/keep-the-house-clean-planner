import { describe, expect, it } from 'vitest';
import { computeDue, dueState, type DueTaskInput } from './due.ts';
import { DEFAULT_INTERVALS } from './schemas/intervals.ts';

const task = (overrides: Partial<DueTaskInput> & Pick<DueTaskInput, '_id'>): DueTaskInput => ({
  active: true,
  intervalKey: '1w',
  lastCompletedAt: null,
  initialDueDate: '2026-09-16',
  ...overrides,
});

describe('dueState', () => {
  it.each([
    [0, 'ok'],
    [0.99, 'ok'],
    [1.0, 'due'],
    [1.49, 'due'],
    [1.5, 'overdue'],
    [12, 'overdue'],
  ] as const)('ratio %d → %s', (ratio, state) => {
    expect(dueState(ratio)).toBe(state);
  });
});

describe('computeDue', () => {
  it('hits the exact boundaries 1.0 and 1.5', () => {
    const results = computeDue(
      [
        task({ _id: 'six', lastCompletedAt: '2026-09-10T10:00:00Z' }), // 6/7
        task({ _id: 'seven', lastCompletedAt: '2026-09-09T10:00:00Z' }), // 7/7 = 1.0
        task({ _id: 'fortnight-20', intervalKey: '2wk', lastCompletedAt: '2026-08-27T10:00:00Z' }), // 20/14
        task({ _id: 'fortnight-21', intervalKey: '2wk', lastCompletedAt: '2026-08-26T10:00:00Z' }), // 21/14 = 1.5
      ],
      DEFAULT_INTERVALS,
      '2026-09-16',
    );
    const byId = Object.fromEntries(results.map((r) => [r.taskId, r]));
    expect(byId.six).toMatchObject({ daysSince: 6, state: 'ok' });
    expect(byId.seven).toMatchObject({ daysSince: 7, ratio: 1, state: 'due' });
    expect(byId['fortnight-20']).toMatchObject({ daysSince: 20, state: 'due' });
    expect(byId['fortnight-21']).toMatchObject({ daysSince: 21, ratio: 1.5, state: 'overdue' });
  });

  it('counts local calendar days across DST changes', () => {
    const results = computeDue(
      [
        // 22:30 local on 24 Oct (CEST) → 26 Oct: 2 calendar days, although 49.5 hours
        task({ _id: 'fall', intervalKey: 'daily', lastCompletedAt: '2026-10-24T20:30:00Z' }),
        // 00:30 local on 29 Mar (CEST starts that night, still CET at 00:30) → 30 Mar: 1 day
        task({ _id: 'spring', intervalKey: 'daily', lastCompletedAt: '2026-03-28T23:30:00Z' }),
      ],
      DEFAULT_INTERVALS,
      '2026-10-26',
    );
    expect(results.find((r) => r.taskId === 'fall')?.daysSince).toBe(2);
    const spring = computeDue(
      [task({ _id: 'spring', intervalKey: 'daily', lastCompletedAt: '2026-03-28T23:30:00Z' })],
      DEFAULT_INTERVALS,
      '2026-03-30',
    );
    expect(spring[0]?.daysSince).toBe(1);
  });

  it('uses the local day, not the UTC day, of the last completion', () => {
    // 23:30 UTC on 15 Sep is already 16 Sep in Amsterdam
    const [result] = computeDue(
      [task({ _id: 'late', intervalKey: 'daily', lastCompletedAt: '2026-09-15T23:30:00Z' })],
      DEFAULT_INTERVALS,
      '2026-09-16',
    );
    expect(result?.daysSince).toBe(0);
  });

  it('uses the explicit first due date when never completed', () => {
    const input = task({ _id: 'new', intervalKey: 'quarter', initialDueDate: '2026-10-16' });
    expect(computeDue([input], DEFAULT_INTERVALS, '2026-09-20')[0]).toMatchObject({
      daysSince: 0,
      ratio: 0,
      state: 'ok',
    });
    expect(computeDue([input], DEFAULT_INTERVALS, '2026-10-16')[0]).toMatchObject({
      daysSince: 91,
      periodDays: 91,
      ratio: 1,
      state: 'due',
    });
  });

  it('ranks by ratio, skips inactive tasks and unknown intervals', () => {
    const results = computeDue(
      [
        task({ _id: 'a', lastCompletedAt: '2026-09-12T10:00:00Z' }), // 4/7
        task({ _id: 'b', intervalKey: 'daily', lastCompletedAt: '2026-09-13T10:00:00Z' }), // 3/1
        task({ _id: 'c', active: false, lastCompletedAt: '2025-01-01T10:00:00Z' }),
        task({ _id: 'd', intervalKey: 'mystery' }),
        task({ _id: 'e', intervalKey: '2wk', lastCompletedAt: '2026-09-02T10:00:00Z' }), // 14/14
      ],
      DEFAULT_INTERVALS,
      '2026-09-16',
    );
    expect(results.map((r) => r.taskId)).toEqual(['b', 'e', 'a']);
  });

  it('never reports negative days for a completion in the future', () => {
    const [result] = computeDue([task({ _id: 'x', lastCompletedAt: '2026-09-20T10:00:00Z' })], DEFAULT_INTERVALS, '2026-09-16');
    expect(result?.daysSince).toBe(0);
  });
});
