import { describe, expect, it } from 'vitest';
import {
  DEFAULT_INTERVALS,
  createTaskInputSchema,
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

  it('discriminates occurrence patch actions', () => {
    expect(patchOccurrenceInputSchema.parse({ action: 'skip', reason: 'ziek' })).toEqual({
      action: 'skip',
      reason: 'ziek',
    });
    expect(patchOccurrenceInputSchema.safeParse({ action: 'reschedule' }).success).toBe(false);
    expect(patchOccurrenceInputSchema.safeParse({ action: 'explode' }).success).toBe(false);
  });
});
