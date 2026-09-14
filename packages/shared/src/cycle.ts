import { addDays, daysBetween, isMonday, sun0ToMon0, type DayKey } from './time.ts';

export const CYCLE_DAYS = 28;
export const CYCLE_WEEKS = 4;

export function assertValidAnchor(anchor: DayKey): void {
  if (!isMonday(anchor)) throw new RangeError(`Cycle anchor must be a Monday: ${anchor}`);
}

function mod(n: number, m: number): number {
  return ((n % m) + m) % m;
}

/** Cycle index of a day; negative for days before the anchor. */
export function cycleIndexFor(date: DayKey, anchor: DayKey): number {
  assertValidAnchor(anchor);
  return Math.floor(daysBetween(anchor, date) / CYCLE_DAYS);
}

/** First day (a Monday) of the cycle. */
export function cycleStart(index: number, anchor: DayKey): DayKey {
  assertValidAnchor(anchor);
  return addDays(anchor, index * CYCLE_DAYS);
}

/** Last day (a Sunday) of the cycle. */
export function cycleEnd(index: number, anchor: DayKey): DayKey {
  return addDays(cycleStart(index, anchor), CYCLE_DAYS - 1);
}

/** Week 0..3 within the cycle. */
export function weekIndexFor(date: DayKey, anchor: DayKey): number {
  assertValidAnchor(anchor);
  return Math.floor(mod(daysBetween(anchor, date), CYCLE_DAYS) / 7);
}

/**
 * Concrete date of a template slot.
 * @param weekday 0=Sunday..6=Saturday; Sunday is the last day of the Monday-first week.
 */
export function slotDate(cycleStartDate: DayKey, weekIndex: number, weekday: number): DayKey {
  if (!Number.isInteger(weekIndex) || weekIndex < 0 || weekIndex >= CYCLE_WEEKS) {
    throw new RangeError(`weekIndex out of range: ${weekIndex}`);
  }
  if (!Number.isInteger(weekday) || weekday < 0 || weekday > 6) {
    throw new RangeError(`weekday out of range: ${weekday}`);
  }
  return addDays(cycleStartDate, weekIndex * 7 + sun0ToMon0(weekday));
}
