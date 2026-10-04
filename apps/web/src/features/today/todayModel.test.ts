import { describe, expect, it } from 'vitest';
import { makeOccurrenceV2 } from '../../test/render.tsx';
import { groupToday } from './todayModel.ts';

const ME = 'a00000000000000000000001';
const OTHER = 'b00000000000000000000002';
const TODAY = '2026-09-16';

describe('groupToday', () => {
  it('splits into mine, unclaimed, others, overdue and finished', () => {
    const list = [
      makeOccurrenceV2({ id: 'o1', taskNameSnapshot: 'Stofzuigen', date: TODAY, assigneeId: OTHER }),
      makeOccurrenceV2({ id: 'o2', taskNameSnapshot: 'Afwas', date: TODAY, assigneeId: ME }),
      makeOccurrenceV2({ id: 'o3', taskNameSnapshot: 'Badkamer', date: '2026-09-14', assigneeId: ME, isOverdue: true }),
      makeOccurrenceV2({ id: 'o4', taskNameSnapshot: 'Wastafel', date: TODAY, assigneeId: null }),
      makeOccurrenceV2({ id: 'o5', taskNameSnapshot: 'Bed', date: TODAY, assigneeId: ME, status: 'done' }),
      makeOccurrenceV2({ id: 'o6', taskNameSnapshot: 'Oud gedaan', date: '2026-09-14', status: 'done' }),
      makeOccurrenceV2({ id: 'o7', taskNameSnapshot: 'Aanrecht', date: TODAY, assigneeId: ME }),
    ];
    const groups = groupToday(list, ME, TODAY);
    const ids = (key: keyof typeof groups) => groups[key].map((o) => o.id);
    expect(ids('mine')).toEqual(['o7', 'o2']); // sorted by name: Aanrecht, Afwas
    expect(ids('unclaimed')).toEqual(['o4']);
    expect(ids('others')).toEqual(['o1']);
    expect(ids('overdue')).toEqual(['o3']);
    expect(ids('finished')).toEqual(['o5']);
  });

  it('puts overdue items in the overdue section regardless of assignee', () => {
    const groups = groupToday(
      [makeOccurrenceV2({ id: 'o1', date: '2026-09-10', assigneeId: null }), makeOccurrenceV2({ id: 'o2', date: '2026-09-15', assigneeId: OTHER })],
      ME,
      TODAY,
    );
    expect(groups.overdue.map((o) => o.id)).toEqual(['o1', 'o2']);
    expect(groups.unclaimed).toEqual([]);
  });

  it('does not call tasks from before the configured cycle start overdue', () => {
    const groups = groupToday(
      [makeOccurrenceV2({ id: 'old', date: '2026-09-14', assigneeId: ME })],
      ME,
      TODAY,
      '2026-09-17',
    );

    expect(groups.overdue).toEqual([]);
  });
});
