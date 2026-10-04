import { describe, expect, it } from 'vitest';
import { bonusLabelOfKey } from './bonusKey.ts';

describe('bonusLabelOfKey', () => {
  const person = 'a00000000000000000000001';

  it('reads the kind and the period from a ledger key, a week ending six days and a cycle 27 days after its first day', () => {
    expect(bonusLabelOfKey(`bonus_week_done:${person}:2026-09-28`)).toEqual({
      key: 'stats.points.bonus.weekDone',
      week: 40,
      from: '2026-09-28',
      to: '2026-10-04',
    });
    expect(bonusLabelOfKey(`bonus_cycle_ontime:${person}:2026-09-07`)).toEqual({
      key: 'stats.points.bonus.cycleOnTime',
      week: 37,
      from: '2026-09-07',
      to: '2026-10-04',
    });
  });

  it('is null for anything that is not a bonus key', () => {
    expect(bonusLabelOfKey(`execution:${person}`)).toBeNull();
    expect(bonusLabelOfKey('bonus_week_done:x')).toBeNull();
    expect(bonusLabelOfKey(`bonus_week_done:${person}:soon`)).toBeNull();
  });
});
