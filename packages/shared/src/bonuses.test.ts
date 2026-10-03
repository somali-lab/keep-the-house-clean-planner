import { describe, expect, it } from 'vitest';
import {
  bonusAmountsOn,
  bonusKey,
  cycleOf,
  evaluateSet,
  expectedBonusEntries,
  onTimeCutoff,
  creditedOf,
  periodDayOf,
  periodOwnerOf,
  placementsOf,
  periodEnded,
  scheduleWithAmounts,
  weekOf,
  type BonusAmounts,
  type BonusContext,
  type BonusOccurrence,
  type BonusScheduleRow,
} from './bonuses.ts';

const ANNA = 'a'.repeat(24);
const BRAM = 'b'.repeat(24);
const CAREL = 'c'.repeat(24);

const ALL: BonusAmounts = { weekDone: 5, weekOnTime: 3, cycleDone: 20, cycleOnTime: 10 };
const SCHEDULE: BonusScheduleRow[] = [{ from: '2026-01-01', ...ALL }];

/** Cycle anchor Monday 14 Sep 2026: cycle 0 is 14 Sep to 11 Oct, cycle 1 is 12 Oct to 8 Nov. */
function context(today: string, overrides: Partial<BonusContext> = {}): BonusContext {
  return { anchor: '2026-09-14', timezone: 'Europe/Amsterdam', today, schedule: SCHEDULE, ...overrides };
}

/** A planned occurrence of Anna, done on time unless told otherwise. */
function occ(plannedDate: string, overrides: Partial<BonusOccurrence> = {}): BonusOccurrence {
  return {
    status: 'done',
    plannedDate,
    date: plannedDate,
    recordedDone: false,
    assigneeId: ANNA,
    completedBy: ANNA,
    completedAt: `${plannedDate}T09:00:00.000Z`,
    ...overrides,
  };
}

const kinds = (entries: ReturnType<typeof expectedBonusEntries>) => entries.map((entry) => `${entry.kind}:${entry.personId === ANNA ? 'anna' : entry.personId === BRAM ? 'bram' : 'carel'}:${entry.periodStart}`);

describe('periods', () => {
  it('finds the week and the cycle of any day, also before the anchor', () => {
    expect(weekOf('2026-09-16')).toEqual({ unit: 'week', start: '2026-09-14', end: '2026-09-20' });
    expect(weekOf('2026-09-20')).toEqual({ unit: 'week', start: '2026-09-14', end: '2026-09-20' });
    expect(cycleOf('2026-10-11', '2026-09-14')).toEqual({ unit: 'cycle', start: '2026-09-14', end: '2026-10-11' });
    expect(cycleOf('2026-10-12', '2026-09-14')).toEqual({ unit: 'cycle', start: '2026-10-12', end: '2026-11-08' });
    expect(cycleOf('2026-09-13', '2026-09-14')).toEqual({ unit: 'cycle', start: '2026-08-17', end: '2026-09-13' });
  });

  it('has ended only when its last day is before today', () => {
    const week = weekOf('2026-09-16');
    expect(periodEnded(week, '2026-09-20')).toBe(false);
    expect(periodEnded(week, '2026-09-21')).toBe(true);
    expect(periodEnded(week, '2026-09-14')).toBe(false);
  });

  it('puts the cut-off at local midnight after the last day, so a DST week is 167 or 169 hours', () => {
    expect(onTimeCutoff({ end: '2026-10-25' }, 'Europe/Amsterdam').toISOString()).toBe('2026-10-25T23:00:00.000Z');
    expect(onTimeCutoff({ end: '2026-03-29' }, 'Europe/Amsterdam').toISOString()).toBe('2026-03-29T22:00:00.000Z');
    expect(onTimeCutoff({ end: '2026-09-20' }, 'Europe/Amsterdam').toISOString()).toBe('2026-09-20T22:00:00.000Z');
    const start = (key: string) => Date.parse(`${key}T00:00:00Z`);
    // Monday 19 Oct 00:00 local is 18 Oct 22:00Z (CEST); the cut-off is 25 Oct 23:00Z: 169 hours.
    expect((Date.parse('2026-10-25T23:00:00.000Z') - (start('2026-10-18') + 22 * 3_600_000)) / 3_600_000).toBe(169);
  });
});

describe('the occurrence in a set', () => {
  it('uses the planned day for planned work and the date for recorded work', () => {
    expect(periodDayOf({ plannedDate: '2026-09-16', date: '2026-09-23', recordedDone: false })).toBe('2026-09-16');
    expect(periodDayOf({ plannedDate: '2026-09-16', date: '2026-09-23' })).toBe('2026-09-16');
    expect(periodDayOf({ plannedDate: '2026-09-16', date: '2026-09-23', recordedDone: true })).toBe('2026-09-23');
  });

  it('takes the frozen period owner, else the assignee, and credits done work to completedBy or the assignee', () => {
    expect(periodOwnerOf({ assigneeId: ANNA })).toBe(ANNA);
    expect(periodOwnerOf({ assigneeId: ANNA, periodOwnerId: BRAM })).toBe(BRAM);
    expect(periodOwnerOf({ assigneeId: ANNA, periodOwnerId: null })).toBeNull();
    expect(creditedOf({ status: 'done', assigneeId: ANNA, completedBy: BRAM })).toBe(BRAM);
    expect(creditedOf({ status: 'done', assigneeId: ANNA, completedBy: null })).toBe(ANNA);
    expect(creditedOf({ status: 'open', assigneeId: ANNA, completedBy: BRAM })).toBeNull();
  });

  it('places work in the set of its owner, and done work by someone else as open for the owner and non-blocking for the doer', () => {
    expect(placementsOf(occ('2026-09-14')).map((p) => p.person)).toEqual([ANNA]);
    expect(placementsOf(occ('2026-09-14', { status: 'skipped', completedBy: null, completedAt: null })).map((p) => p.person)).toEqual([ANNA]);
    expect(placementsOf(occ('2026-09-14', { status: 'open', assigneeId: null, completedBy: null, completedAt: null }))).toEqual([]);
    const byBram = placementsOf(occ('2026-09-14', { completedBy: BRAM }));
    expect(byBram.map((p) => [p.person, p.item.status, p.item.recordedDone === true])).toEqual([
      [ANNA, 'open', false],
      [BRAM, 'done', true],
    ]);
    // Unassigned owner: only the doer has an item, and it is non-blocking.
    expect(placementsOf(occ('2026-09-14', { assigneeId: null, periodOwnerId: null, completedBy: BRAM })).map((p) => p.person)).toEqual([BRAM]);
  });
});

describe('evaluateSet', () => {
  const cutoff = new Date('2026-09-20T22:00:00.000Z');

  it('counts the states and needs a planned occurrence to be eligible', () => {
    const set = [occ('2026-09-14'), occ('2026-09-15', { status: 'open', completedAt: null }), occ('2026-09-16', { status: 'skipped', completedAt: null })];
    expect(evaluateSet(set, cutoff)).toEqual({
      total: 3, planned: 3, done: 1, open: 1, skipped: 1, eligible: true, allDone: false, allOnTime: false,
    });
    expect(evaluateSet([], cutoff)).toMatchObject({ total: 0, eligible: false, allDone: false, allOnTime: false });
    const recorded = [occ('2026-09-14', { recordedDone: true })];
    expect(evaluateSet(recorded, cutoff)).toMatchObject({ total: 1, planned: 0, eligible: false, allDone: true, allOnTime: true });
  });

  it('is done but not on time at or after the cut-off, and for done work without completedAt', () => {
    expect(evaluateSet([occ('2026-09-14', { completedAt: '2026-09-20T21:59:59.999Z' })], cutoff)).toMatchObject({ allDone: true, allOnTime: true });
    expect(evaluateSet([occ('2026-09-14', { completedAt: '2026-09-20T22:00:00.000Z' })], cutoff)).toMatchObject({ allDone: true, allOnTime: false });
    expect(evaluateSet([occ('2026-09-14', { completedAt: null })], cutoff)).toMatchObject({ allDone: true, allOnTime: false });
  });
});

describe('the amounts in force', () => {
  const schedule: BonusScheduleRow[] = [
    { from: '2026-09-01', weekDone: 1, weekOnTime: 2, cycleDone: 3, cycleOnTime: 4 },
    { from: '2026-10-01', weekDone: 5, weekOnTime: 6, cycleDone: 7, cycleOnTime: 8 },
  ];

  it('takes the last row on or before the day, and zero before the first row', () => {
    expect(bonusAmountsOn(schedule, '2026-08-31')).toEqual({ weekDone: 0, weekOnTime: 0, cycleDone: 0, cycleOnTime: 0 });
    expect(bonusAmountsOn(schedule, '2026-09-01')).toMatchObject({ weekDone: 1 });
    expect(bonusAmountsOn(schedule, '2026-09-30')).toMatchObject({ weekDone: 1 });
    expect(bonusAmountsOn(schedule, '2026-10-01')).toMatchObject({ weekDone: 5 });
    expect(bonusAmountsOn([], '2026-10-01')).toEqual({ weekDone: 0, weekOnTime: 0, cycleDone: 0, cycleOnTime: 0 });
  });

  it('writes a row from today only when the amounts differ from the row in force', () => {
    expect(scheduleWithAmounts([], { weekDone: 0, weekOnTime: 0, cycleDone: 0, cycleOnTime: 0 }, '2026-10-05')).toEqual([]);
    expect(scheduleWithAmounts(schedule, { weekDone: 5, weekOnTime: 6, cycleDone: 7, cycleOnTime: 8 }, '2026-10-05')).toEqual(schedule);
    expect(scheduleWithAmounts(schedule, { weekDone: 9, weekOnTime: 6, cycleDone: 7, cycleOnTime: 8 }, '2026-10-05')).toEqual([
      ...schedule,
      { from: '2026-10-05', weekDone: 9, weekOnTime: 6, cycleDone: 7, cycleOnTime: 8 },
    ]);
  });

  it('replaces the row that already starts today and keeps the list sorted', () => {
    expect(scheduleWithAmounts(schedule, { weekDone: 9, weekOnTime: 9, cycleDone: 9, cycleOnTime: 9 }, '2026-10-01')).toEqual([
      schedule[0],
      { from: '2026-10-01', weekDone: 9, weekOnTime: 9, cycleDone: 9, cycleOnTime: 9 },
    ]);
    expect(scheduleWithAmounts(schedule, { weekDone: 9, weekOnTime: 9, cycleDone: 9, cycleOnTime: 9 }, '2026-09-15').map((row) => row.from)).toEqual([
      '2026-09-01',
      '2026-09-15',
      '2026-10-01',
    ]);
  });
});

describe('expectedBonusEntries', () => {
  it('gives an on-time week both week bonuses, in addition to each other', () => {
    const entries = expectedBonusEntries([occ('2026-09-14'), occ('2026-09-16')], context('2026-09-21'));
    expect(kinds(entries)).toEqual(['bonus_week_done:anna:2026-09-14', 'bonus_week_ontime:anna:2026-09-14']);
    expect(entries[0]).toEqual({
      key: bonusKey('bonus_week_done', ANNA, '2026-09-14'),
      kind: 'bonus_week_done',
      personId: ANNA,
      amount: 5,
      periodStart: '2026-09-14',
      periodEnd: '2026-09-20',
    });
    expect(entries[1]).toMatchObject({ amount: 3 });
    expect(entries[0]!.key).toBe(`bonus_week_done:${ANNA}:2026-09-14`);
  });

  it('does not pay a period that has not ended, also when it is complete', () => {
    const items = [occ('2026-09-14')];
    expect(expectedBonusEntries(items, context('2026-09-20'))).toEqual([]);
    expect(expectedBonusEntries(items, context('2026-09-14'))).toEqual([]);
    expect(expectedBonusEntries(items, context('2026-09-21'))).toHaveLength(2);
  });

  it('pays nothing for an empty set', () => {
    expect(expectedBonusEntries([], context('2026-09-21'))).toEqual([]);
  });

  it('needs a planned occurrence: recorded work alone earns nothing but still counts inside a set', () => {
    const recorded = occ('2026-09-16', { recordedDone: true, plannedDate: '2026-09-16' });
    expect(expectedBonusEntries([recorded], context('2026-09-21'))).toEqual([]);
    expect(kinds(expectedBonusEntries([recorded, occ('2026-09-14')], context('2026-09-21')))).toEqual([
      'bonus_week_done:anna:2026-09-14',
      'bonus_week_ontime:anna:2026-09-14',
    ]);
    // Recorded work is placed by its date, so it cannot block, and it is never late.
    const late = occ('2026-09-10', { recordedDone: true, date: '2026-09-16', completedAt: '2026-09-16T10:00:00.000Z' });
    expect(kinds(expectedBonusEntries([late, occ('2026-09-14')], context('2026-09-21')))).toHaveLength(2);
  });

  describe('DST weeks', () => {
    it('week ending 2026-10-25 (autumn, 169 hours): 22:59Z is on time and 23:00Z is late', () => {
      const onTime = [occ('2026-10-19', { completedAt: '2026-10-25T22:59:59.999Z' })];
      const late = [occ('2026-10-19', { completedAt: '2026-10-25T23:00:00.000Z' })];
      expect(kinds(expectedBonusEntries(onTime, context('2026-10-26')))).toEqual(['bonus_week_done:anna:2026-10-19', 'bonus_week_ontime:anna:2026-10-19']);
      expect(kinds(expectedBonusEntries(late, context('2026-10-26')))).toEqual(['bonus_week_done:anna:2026-10-19']);
    });

    it('week ending 2026-03-29 (spring, 167 hours): 21:59Z is on time and 22:00Z is late; the cycle ends the same day', () => {
      const onTime = [occ('2026-03-23', { completedAt: '2026-03-29T21:59:59.999Z' })];
      const late = [occ('2026-03-23', { completedAt: '2026-03-29T22:00:00.000Z' })];
      expect(kinds(expectedBonusEntries(onTime, context('2026-03-30')))).toEqual([
        'bonus_cycle_done:anna:2026-03-02',
        'bonus_cycle_ontime:anna:2026-03-02',
        'bonus_week_done:anna:2026-03-23',
        'bonus_week_ontime:anna:2026-03-23',
      ]);
      expect(kinds(expectedBonusEntries(late, context('2026-03-30')))).toEqual(['bonus_cycle_done:anna:2026-03-02', 'bonus_week_done:anna:2026-03-23']);
    });
  });

  describe('cycles', () => {
    const cycleWeeks = ['2026-09-14', '2026-09-21', '2026-09-28', '2026-10-05'];
    const allDone = cycleWeeks.map((day) => occ(day));

    it('pays the cycle bonuses and the four week bonuses once the cycle has ended', () => {
      const entries = expectedBonusEntries(allDone, context('2026-10-12'));
      expect(entries.filter((entry) => entry.kind.startsWith('bonus_cycle'))).toEqual([
        expect.objectContaining({ key: `bonus_cycle_done:${ANNA}:2026-09-14`, amount: 20, periodStart: '2026-09-14', periodEnd: '2026-10-11' }),
        expect.objectContaining({ key: `bonus_cycle_ontime:${ANNA}:2026-09-14`, amount: 10 }),
      ]);
      expect(entries.filter((entry) => entry.kind.startsWith('bonus_week'))).toHaveLength(8);
      // The last week ends with the cycle, so the cycle has not ended on its last day.
      expect(expectedBonusEntries(allDone, context('2026-10-11')).filter((entry) => entry.kind.startsWith('bonus_cycle'))).toEqual([]);
    });

    it('counts a partial first cycle in full: a shorter set earns the whole amount', () => {
      const entries = expectedBonusEntries([occ('2026-10-05')], context('2026-10-12'));
      expect(entries.map((entry) => entry.key).sort()).toEqual(
        [
          `bonus_cycle_done:${ANNA}:2026-09-14`,
          `bonus_cycle_ontime:${ANNA}:2026-09-14`,
          `bonus_week_done:${ANNA}:2026-10-05`,
          `bonus_week_ontime:${ANNA}:2026-10-05`,
        ].sort(),
      );
      expect(entries).toHaveLength(4);
      expect(entries.find((entry) => entry.kind === 'bonus_cycle_done')).toMatchObject({ amount: 20, periodStart: '2026-09-14' });
    });

    it('keys a cycle before the anchor by its first day, with a negative index', () => {
      const entries = expectedBonusEntries([occ('2026-08-20')], context('2026-09-14'));
      expect(entries.filter((entry) => entry.kind.startsWith('bonus_cycle')).map((entry) => [entry.key, entry.periodStart, entry.periodEnd])).toEqual([
        [`bonus_cycle_done:${ANNA}:2026-08-17`, '2026-08-17', '2026-09-13'],
        [`bonus_cycle_ontime:${ANNA}:2026-08-17`, '2026-08-17', '2026-09-13'],
      ]);
    });

    it('blocks the cycle bonus when one occurrence of the cycle is open, but not the weeks that are complete', () => {
      const items = [occ('2026-09-14'), occ('2026-09-21'), occ('2026-09-28', { status: 'open', completedAt: null, completedBy: null }), occ('2026-10-05')];
      const entries = expectedBonusEntries(items, context('2026-10-12'));
      expect(entries.filter((entry) => entry.kind.startsWith('bonus_cycle'))).toEqual([]);
      expect(entries.filter((entry) => entry.kind === 'bonus_week_done').map((entry) => entry.periodStart)).toEqual(['2026-09-14', '2026-09-21', '2026-10-05']);
    });
  });

  describe('what is done and what is on time', () => {
    it('does not pay open or skipped work, but pays work that was skipped and completed later', () => {
      const skipped = occ('2026-09-14', { status: 'skipped', completedAt: null, completedBy: null });
      expect(expectedBonusEntries([skipped, occ('2026-09-15')], context('2026-09-21'))).toEqual([]);
      // Skipped, then completed after the week: done, but late.
      const completedLater = occ('2026-09-14', { completedAt: '2026-09-25T10:00:00.000Z' });
      expect(kinds(expectedBonusEntries([completedLater, occ('2026-09-15')], context('2026-09-26')))).toEqual(['bonus_week_done:anna:2026-09-14']);
    });

    it('pays "done" but not "on time" for a late check-off, in the week and the cycle alike when it is past the cycle', () => {
      const lateInCycle = [occ('2026-09-14', { completedAt: '2026-10-12T10:00:00.000Z' })];
      const entries = expectedBonusEntries(lateInCycle, context('2026-10-13'));
      expect(entries.map((entry) => entry.kind).sort()).toEqual(['bonus_cycle_done', 'bonus_week_done']);
    });

    it('treats done work without completedAt as done but never on time', () => {
      const entries = expectedBonusEntries([occ('2026-09-14', { completedAt: null })], context('2026-09-21'));
      expect(entries.map((entry) => entry.kind)).toEqual(['bonus_week_done']);
    });

    it('rescheduled N to N+1: late for the week, on time for the cycle when inside it', () => {
      // Planned in week 1 of the cycle, dragged to week 2 and completed there.
      const moved = occ('2026-09-16', { date: '2026-09-23', completedAt: '2026-09-23T10:00:00.000Z' });
      const entries = expectedBonusEntries([moved], context('2026-10-12'));
      const byKind = (kind: string) => entries.filter((entry) => entry.kind === kind).map((entry) => entry.periodStart);
      expect(byKind('bonus_week_done')).toEqual(['2026-09-14']);
      expect(byKind('bonus_week_ontime')).toEqual([]);
      expect(byKind('bonus_cycle_done')).toEqual(['2026-09-14']);
      expect(byKind('bonus_cycle_ontime')).toEqual(['2026-09-14']);
    });

    it('rescheduled and still open: it keeps blocking the week it was planned in and never counts in the next one', () => {
      const moved = occ('2026-09-16', { date: '2026-09-23', status: 'open', completedAt: null, completedBy: null });
      expect(expectedBonusEntries([moved, occ('2026-09-14')], context('2026-09-21'))).toEqual([]);
      // The week of the new date holds nothing of it: another person's complete week is paid on its own.
      expect(kinds(expectedBonusEntries([moved, occ('2026-09-21', { assigneeId: BRAM, completedBy: BRAM })], context('2026-09-28')))).toEqual([
        'bonus_week_done:bram:2026-09-21',
        'bonus_week_ontime:bram:2026-09-21',
      ]);
    });
  });

  describe('who the work belongs to', () => {
    it('credits a check-off for the assignee, and a take-over to the actor without blocking the assignee', () => {
      // Anna's task, checked off by Bram as a take-over: it is in Bram's set, and leaves Anna's.
      const takenOver = occ('2026-09-14', { assigneeId: BRAM, completedBy: BRAM });
      const forAnna = occ('2026-09-15');
      expect(kinds(expectedBonusEntries([takenOver, forAnna], context('2026-09-21')))).toEqual([
        'bonus_week_done:anna:2026-09-14',
        'bonus_week_done:bram:2026-09-14',
        'bonus_week_ontime:anna:2026-09-14',
        'bonus_week_ontime:bram:2026-09-14',
      ]);
    });

    it('credits a named third person without needing the work, and the assignee has not done it', () => {
      const byCarel = occ('2026-09-14', { assigneeId: ANNA, completedBy: CAREL });
      // Carel has only non-blocking work, so he is not eligible; Anna's item is not done.
      expect(expectedBonusEntries([byCarel], context('2026-09-21'))).toEqual([]);
      const withOwn = [byCarel, occ('2026-09-15', { assigneeId: CAREL, completedBy: CAREL })];
      expect(new Set(expectedBonusEntries(withOwn, context('2026-09-21')).map((entry) => entry.personId))).toEqual(new Set([CAREL]));
    });

    it('lets unassigned open work block nobody and unassigned done work count for the person who did it', () => {
      const anyone = occ('2026-09-15', { status: 'open', assigneeId: null, completedBy: null, completedAt: null });
      expect(kinds(expectedBonusEntries([occ('2026-09-14'), anyone], context('2026-09-21')))).toHaveLength(2);
      const claimed = occ('2026-09-15', { assigneeId: null, completedBy: BRAM });
      const entries = expectedBonusEntries([claimed, occ('2026-09-14', { assigneeId: BRAM, completedBy: BRAM })], context('2026-09-21'));
      expect(new Set(entries.map((entry) => entry.personId))).toEqual(new Set([BRAM]));
      // Unassigned done work without anybody credited belongs to nobody.
      expect(expectedBonusEntries([occ('2026-09-15', { assigneeId: null, completedBy: null })], context('2026-09-21'))).toEqual([]);
    });

    it('evaluates every person on their own set', () => {
      const items = [occ('2026-09-14'), occ('2026-09-15', { assigneeId: BRAM, completedBy: null, status: 'open', completedAt: null })];
      expect(kinds(expectedBonusEntries(items, context('2026-09-21')))).toEqual(['bonus_week_done:anna:2026-09-14', 'bonus_week_ontime:anna:2026-09-14']);
    });
  });

  describe('helping with overdue work from an ended week', () => {
    // Anna and Bram both have a finished week; Bram's second item stayed open and is overdue.
    const annaDone = occ('2026-09-14');
    const bramDone = occ('2026-09-14', { assigneeId: BRAM, completedBy: BRAM });
    const bramOverdue = (overrides: Partial<BonusOccurrence>) =>
      occ('2026-09-15', { assigneeId: BRAM, periodOwnerId: BRAM, status: 'open', completedBy: null, completedAt: null, ...overrides });

    it("keeps Anna's finalised week bonus when she takes over Bram's overdue item, and Bram gains nothing from it", () => {
      const takenOver = bramOverdue({ status: 'done', assigneeId: ANNA, completedBy: ANNA, completedAt: '2026-09-23T10:00:00.000Z' });
      const entries = expectedBonusEntries([annaDone, bramDone, takenOver], context('2026-09-28'));
      // Anna: her own item is done on time and the taken-over one is non-blocking.
      expect(kinds(entries.filter((entry) => entry.personId === ANNA))).toEqual(['bonus_week_done:anna:2026-09-14', 'bonus_week_ontime:anna:2026-09-14']);
      // Bram: the item he missed is not done for him, so he earns nothing.
      expect(entries.filter((entry) => entry.personId === BRAM)).toEqual([]);
    });

    it('does not turn that item into a bonus for Anna on its own', () => {
      const takenOver = bramOverdue({ status: 'done', assigneeId: ANNA, completedBy: ANNA, completedAt: '2026-09-23T10:00:00.000Z' });
      expect(expectedBonusEntries([takenOver], context('2026-09-28'))).toEqual([]);
    });

    it('treats unassigned overdue work that is claimed as non-blocking for the claimer and for nobody else', () => {
      const claimed = occ('2026-09-15', { assigneeId: BRAM, periodOwnerId: null, completedBy: BRAM, completedAt: '2026-09-23T10:00:00.000Z' });
      const entries = expectedBonusEntries([bramDone, claimed], context('2026-09-28'));
      expect(kinds(entries)).toEqual(['bonus_week_done:bram:2026-09-14', 'bonus_week_ontime:bram:2026-09-14']);
      // Still unassigned and open after the week: it blocks nobody.
      const open = occ('2026-09-15', { assigneeId: null, periodOwnerId: null, status: 'open', completedBy: null, completedAt: null });
      expect(kinds(expectedBonusEntries([bramDone, open], context('2026-09-28')))).toHaveLength(2);
    });

    it("does not move reassigned open overdue work into the new assignee's ended week", () => {
      const reassigned = bramOverdue({ assigneeId: ANNA });
      const entries = expectedBonusEntries([annaDone, bramDone, reassigned], context('2026-09-28'));
      // Bram still owns the missed item for that week; Anna's week is unaffected.
      expect(entries.filter((entry) => entry.personId === BRAM)).toEqual([]);
      expect(kinds(entries.filter((entry) => entry.personId === ANNA))).toEqual(['bonus_week_done:anna:2026-09-14', 'bonus_week_ontime:anna:2026-09-14']);
    });

    it('lets Bram finish his own overdue item late: done, but not on time', () => {
      const late = bramOverdue({ status: 'done', completedBy: BRAM, completedAt: '2026-09-23T10:00:00.000Z' });
      expect(kinds(expectedBonusEntries([bramDone, late], context('2026-09-28')))).toEqual(['bonus_week_done:bram:2026-09-14']);
    });

    it('still credits the owner normally for a completion on behalf of the owner', () => {
      const onBehalf = bramOverdue({ status: 'done', completedBy: BRAM, completedAt: '2026-09-18T10:00:00.000Z' });
      expect(kinds(expectedBonusEntries([bramDone, onBehalf], context('2026-09-28')))).toEqual(['bonus_week_done:bram:2026-09-14', 'bonus_week_ontime:bram:2026-09-14']);
    });
  });

  describe('recorded work', () => {
    it('never blocks and is always on time, also when a corrected date lies before its completion instant', () => {
      // An administrator moved the date to the week of 14 September; it was completed in the week after.
      const corrected = occ('2026-09-16', { recordedDone: true, date: '2026-09-16', completedAt: '2026-09-23T10:00:00.000Z' });
      expect(evaluateSet([corrected], new Date('2026-09-20T22:00:00.000Z'))).toMatchObject({ allDone: true, allOnTime: true, eligible: false });
      expect(kinds(expectedBonusEntries([corrected, occ('2026-09-14')], context('2026-09-28')))).toEqual([
        'bonus_week_done:anna:2026-09-14',
        'bonus_week_ontime:anna:2026-09-14',
      ]);
      // Even a recorded item that is not marked done does not block.
      const odd = occ('2026-09-16', { recordedDone: true, status: 'open', completedAt: null });
      expect(evaluateSet([odd], new Date('2026-09-20T22:00:00.000Z'))).toMatchObject({ allDone: true, allOnTime: true });
    });
  });

  describe('the statistics reset floor', () => {
    it('skips the periods that start before the floor, and keeps the ones that start on it', () => {
      const items = [occ('2026-09-14'), occ('2026-09-21')];
      const all = expectedBonusEntries(items, context('2026-09-28'));
      expect(all.map((entry) => entry.periodStart)).toEqual(['2026-09-14', '2026-09-14', '2026-09-21', '2026-09-21']);
      const floored = expectedBonusEntries(items, context('2026-09-28', { floor: '2026-09-21' }));
      expect(floored.map((entry) => entry.periodStart)).toEqual(['2026-09-21', '2026-09-21']);
      // A cycle that starts before the floor is skipped as a whole.
      expect(expectedBonusEntries(items, context('2026-10-12', { floor: '2026-09-21' })).some((entry) => entry.kind.startsWith('bonus_cycle'))).toBe(false);
      expect(expectedBonusEntries(items, context('2026-10-12', { floor: '2026-09-14' })).some((entry) => entry.kind.startsWith('bonus_cycle'))).toBe(true);
    });
  });

  describe('the amounts', () => {
    it('writes no entry for an amount of 0, per kind', () => {
      const schedule: BonusScheduleRow[] = [{ from: '2026-01-01', weekDone: 0, weekOnTime: 3, cycleDone: 0, cycleOnTime: 0 }];
      expect(kinds(expectedBonusEntries([occ('2026-09-14')], context('2026-09-21', { schedule })))).toEqual(['bonus_week_ontime:anna:2026-09-14']);
    });

    it('writes nothing before the first schedule row or with no schedule at all', () => {
      expect(expectedBonusEntries([occ('2026-09-14')], context('2026-09-21', { schedule: [] }))).toEqual([]);
      const later: BonusScheduleRow[] = [{ from: '2026-09-21', ...ALL }];
      expect(expectedBonusEntries([occ('2026-09-14')], context('2026-09-28', { schedule: later }))).toEqual([]);
      // A period ending on the day the row starts is in force.
      expect(expectedBonusEntries([occ('2026-09-14')], context('2026-09-28', { schedule: [{ from: '2026-09-20', ...ALL }] }))).toHaveLength(2);
    });

    it('takes the amounts of the day the period ended, so a later change never alters an ended period', () => {
      const schedule: BonusScheduleRow[] = [
        { from: '2026-01-01', weekDone: 5, weekOnTime: 0, cycleDone: 0, cycleOnTime: 0 },
        { from: '2026-09-21', weekDone: 50, weekOnTime: 0, cycleDone: 0, cycleOnTime: 0 },
      ];
      const items = [occ('2026-09-14'), occ('2026-09-21')];
      const entries = expectedBonusEntries(items, context('2026-09-28', { schedule }));
      expect(entries.map((entry) => [entry.periodStart, entry.amount])).toEqual([['2026-09-14', 5], ['2026-09-21', 50]]);
    });
  });

  it('returns the entries in a deterministic order, whatever the order of the input', () => {
    const items = [occ('2026-09-21', { assigneeId: BRAM, completedBy: BRAM }), occ('2026-09-14'), occ('2026-09-21')];
    const forward = expectedBonusEntries(items, context('2026-09-28'));
    expect(expectedBonusEntries([...items].reverse(), context('2026-09-28'))).toEqual(forward);
    expect(forward.map((entry) => entry.periodEnd)).toEqual([...forward.map((entry) => entry.periodEnd)].sort());
  });
});
