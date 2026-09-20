import { describe, expect, it } from 'vitest';
import { makeOccurrence } from '../../test/render.tsx';
import { addDaysKey, dayKeyInZone, groupToday } from './todayModel.ts';

const ME = 'a00000000000000000000001';
const OTHER = 'b00000000000000000000002';
const TODAY = '2026-09-16';

describe('day keys', () => {
  it('uses the timezone, not UTC', () => {
    expect(dayKeyInZone(new Date('2026-09-15T22:30:00Z'), 'Europe/Amsterdam')).toBe('2026-09-16');
    expect(dayKeyInZone(new Date('2026-09-15T22:30:00Z'), 'UTC')).toBe('2026-09-15');
  });

  it('adds days across month, year and DST boundaries', () => {
    expect(addDaysKey('2026-09-30', 1)).toBe('2026-10-01');
    expect(addDaysKey('2026-10-24', 2)).toBe('2026-10-26');
    expect(addDaysKey('2027-01-01', -1)).toBe('2026-12-31');
  });
});

describe('groupToday', () => {
  it('splits into mine, unclaimed, others, overdue and finished', () => {
    const list = [
      makeOccurrence({ _id: 'o1', taskNameSnapshot: 'Stofzuigen', date: TODAY, assigneeId: OTHER }),
      makeOccurrence({ _id: 'o2', taskNameSnapshot: 'Afwas', date: TODAY, assigneeId: ME }),
      makeOccurrence({ _id: 'o3', taskNameSnapshot: 'Badkamer', date: '2026-09-14', assigneeId: ME, isOverdue: true }),
      makeOccurrence({ _id: 'o4', taskNameSnapshot: 'Wastafel', date: TODAY, assigneeId: null }),
      makeOccurrence({ _id: 'o5', taskNameSnapshot: 'Bed', date: TODAY, assigneeId: ME, status: 'done' }),
      makeOccurrence({ _id: 'o6', taskNameSnapshot: 'Oud gedaan', date: '2026-09-14', status: 'done' }),
      makeOccurrence({ _id: 'o7', taskNameSnapshot: 'Aanrecht', date: TODAY, assigneeId: ME }),
    ];
    const groups = groupToday(list, ME, TODAY);
    const ids = (key: keyof typeof groups) => groups[key].map((o) => o._id);
    expect(ids('mine')).toEqual(['o7', 'o2']); // sorted by name: Aanrecht, Afwas
    expect(ids('unclaimed')).toEqual(['o4']);
    expect(ids('others')).toEqual(['o1']);
    expect(ids('overdue')).toEqual(['o3']);
    expect(ids('finished')).toEqual(['o5']);
  });

  it('puts overdue items in the overdue section regardless of assignee', () => {
    const groups = groupToday(
      [makeOccurrence({ _id: 'o1', date: '2026-09-10', assigneeId: null }), makeOccurrence({ _id: 'o2', date: '2026-09-15', assigneeId: OTHER })],
      ME,
      TODAY,
    );
    expect(groups.overdue.map((o) => o._id)).toEqual(['o1', 'o2']);
    expect(groups.unclaimed).toEqual([]);
  });

  it('does not call tasks from before the configured cycle start overdue', () => {
    const groups = groupToday(
      [makeOccurrence({ _id: 'old', date: '2026-09-14', assigneeId: ME })],
      ME,
      TODAY,
      '2026-09-17',
    );

    expect(groups.overdue).toEqual([]);
  });
});
