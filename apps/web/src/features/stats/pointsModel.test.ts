import { describe, expect, it } from 'vitest';
import { pointsRange } from './pointsModel.ts';

const ANCHOR = '2026-09-14';

describe('pointsRange', () => {
  it('covers the current week and the weeks before it', () => {
    // Wednesday 16 Sep 2026.
    expect(pointsRange({ unit: 'weeks', count: 1 }, ANCHOR, '2026-09-16')).toEqual({ from: '2026-09-14', to: '2026-09-20' });
    expect(pointsRange({ unit: 'weeks', count: 3 }, ANCHOR, '2026-09-16')).toEqual({ from: '2026-08-31', to: '2026-09-20' });
    // A Sunday still belongs to the week that started on Monday.
    expect(pointsRange({ unit: 'weeks', count: 1 }, ANCHOR, '2026-09-20')).toEqual({ from: '2026-09-14', to: '2026-09-20' });
  });

  it('covers the current cycle and the cycles before it, never before the first cycle', () => {
    expect(pointsRange({ unit: 'cycles', count: 1 }, ANCHOR, '2026-10-12')).toEqual({ from: '2026-10-12', to: '2026-11-08' });
    expect(pointsRange({ unit: 'cycles', count: 2 }, ANCHOR, '2026-10-12')).toEqual({ from: '2026-09-14', to: '2026-11-08' });
    expect(pointsRange({ unit: 'cycles', count: 13 }, ANCHOR, '2026-10-12')).toEqual({ from: '2026-09-14', to: '2026-11-08' });
    // Before the anchor there is no cycle yet: the first one is shown.
    expect(pointsRange({ unit: 'cycles', count: 1 }, ANCHOR, '2026-08-01')).toEqual({ from: '2026-09-14', to: '2026-10-11' });
  });

  it('stays within the 371 days the entries endpoint accepts for the largest period', () => {
    // The 13th cycle after the anchor is the last day of a 13-cycle window: 364 days.
    const { from, to } = pointsRange({ unit: 'cycles', count: 13 }, ANCHOR, '2027-08-30');
    expect(from).toBe('2026-09-14');
    expect(to).toBe('2027-09-12');
  });
});
