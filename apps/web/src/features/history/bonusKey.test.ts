import { describe, expect, it } from 'vitest';
import { bonusLabelOfKey } from './bonusKey.ts';

describe('bonusLabelOfKey', () => {
  const person = 'a00000000000000000000001';
  // The ISO week numbers come from the calendar of the server; the model only asks for them.
  const weeks: Record<string, number> = { '2026-09-28': 40, '2026-09-07': 37 };
  const context = { weekOf: (dayKey: string) => weeks[dayKey] ?? null, cycleDays: 28 };

  it('reads the kind and the period from a ledger key, a week ending six days after its first day and a cycle a cycle length after it', () => {
    expect(bonusLabelOfKey(`bonus_week_done:${person}:2026-09-28`, context)).toEqual({
      key: 'stats.points.bonus.weekDone',
      week: 40,
      from: '2026-09-28',
      to: '2026-10-04',
    });
    expect(bonusLabelOfKey(`bonus_cycle_ontime:${person}:2026-09-07`, context)).toEqual({
      key: 'stats.points.bonus.cycleOnTime',
      week: 0,
      from: '2026-09-07',
      to: '2026-10-04',
    });
  });

  it('follows the cycle length of the server', () => {
    expect(bonusLabelOfKey(`bonus_cycle_done:${person}:2026-09-07`, { ...context, cycleDays: 14 })?.to).toBe('2026-09-20');
  });

  it('is null while the week or the cycle length is not known yet', () => {
    expect(bonusLabelOfKey(`bonus_week_done:${person}:2026-10-05`, context)).toBeNull();
    expect(bonusLabelOfKey(`bonus_week_done:${person}:2026-09-28`, { cycleDays: 28 })).toBeNull();
    expect(bonusLabelOfKey(`bonus_cycle_done:${person}:2026-09-07`, { weekOf: context.weekOf })).toBeNull();
  });

  it('is null for anything that is not a bonus key', () => {
    expect(bonusLabelOfKey(`execution:${person}`, context)).toBeNull();
    expect(bonusLabelOfKey('bonus_week_done:x', context)).toBeNull();
    expect(bonusLabelOfKey(`bonus_week_done:${person}:soon`, context)).toBeNull();
  });
});
