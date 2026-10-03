import { cycleEnd, cycleIndexFor, cycleStart } from './cycle.ts';
import { addDays, fromDayKey, mondayOf, type DayKey } from './time.ts';

/**
 * Week and cycle bonuses (ADR-0012): pure rules over day keys, ISO instants and string ids.
 * Every calendar calculation goes through the shared day-key and cycle helpers, so DST is
 * handled where a day key becomes an instant and nowhere else.
 */

/** Smallest and largest amount of one bonus kind. 0 disables the kind. */
export const MIN_BONUS_POINTS = 0;
export const MAX_BONUS_POINTS = 1000;

/** The four derived ledger kinds that join `execution` (ADR-0012). */
export const BONUS_KINDS = ['bonus_week_done', 'bonus_week_ontime', 'bonus_cycle_done', 'bonus_cycle_ontime'] as const;
export type BonusKind = (typeof BONUS_KINDS)[number];

export function isBonusKind(kind: string): kind is BonusKind {
  return (BONUS_KINDS as readonly string[]).includes(kind);
}

export interface BonusAmounts {
  weekDone: number;
  weekOnTime: number;
  cycleDone: number;
  cycleOnTime: number;
}

/** Amounts that apply to every period whose last day is on or after `from`, until the next row. */
export interface BonusScheduleRow extends BonusAmounts {
  from: DayKey;
}

export const NO_BONUSES: BonusAmounts = { weekDone: 0, weekOnTime: 0, cycleDone: 0, cycleOnTime: 0 };

const AMOUNT_OF_KIND: Record<BonusKind, keyof BonusAmounts> = {
  bonus_week_done: 'weekDone',
  bonus_week_ontime: 'weekOnTime',
  bonus_cycle_done: 'cycleDone',
  bonus_cycle_ontime: 'cycleOnTime',
};

export type PeriodUnit = 'week' | 'cycle';

/** A calendar week (Monday to Sunday) or a cycle (28 days from a Monday); identified by its first day. */
export interface Period {
  unit: PeriodUnit;
  start: DayKey;
  end: DayKey;
}

/** The calendar week of a day. */
export function weekOf(day: DayKey): Period {
  const start = mondayOf(day);
  return { unit: 'week', start, end: addDays(start, 6) };
}

/** The cycle of a day, for any day (also before the anchor, where indexes are negative). */
export function cycleOf(day: DayKey, anchor: DayKey): Period {
  const index = cycleIndexFor(day, anchor);
  return { unit: 'cycle', start: cycleStart(index, anchor), end: cycleEnd(index, anchor) };
}

/** A period has ended when its last day is before today; a period ending today has not ended. */
export function periodEnded(period: Pick<Period, 'end'>, todayKey: DayKey): boolean {
  return period.end < todayKey;
}

/**
 * The on-time cut-off of a period: local midnight after its last day, in the household timezone.
 * A DST week is therefore 167 or 169 hours long and Sunday 23:59 local time is always on time.
 */
export function onTimeCutoff(period: Pick<Period, 'end'>, timezone: string): Date {
  return fromDayKey(addDays(period.end, 1), timezone);
}

/** The part of an occurrence that bonuses need. Instants are ISO strings, days are day keys. */
export interface BonusOccurrence {
  status: 'open' | 'done' | 'skipped';
  /** The slot day; survives a reschedule. */
  plannedDate: DayKey;
  /** Where the occurrence stands now. */
  date: DayKey;
  /** Created directly in the done state (no plan). Missing on older data means false. */
  recordedDone?: boolean | undefined;
  assigneeId: string | null;
  /**
   * The assignee at the moment the planned week had ended and the occurrence was first assigned,
   * claimed, taken over or completed (null = it was unassigned). Missing means the assignee.
   */
  periodOwnerId?: string | null | undefined;
  completedBy: string | null;
  completedAt: string | null;
}

/** The day that decides the week and the cycle of an occurrence: the plan for planned work, the date for recorded work. */
export function periodDayOf(occurrence: Pick<BonusOccurrence, 'plannedDate' | 'date' | 'recordedDone'>): DayKey {
  return occurrence.recordedDone === true ? occurrence.date : occurrence.plannedDate;
}

/** The person the occurrence is planned for as far as a period is concerned: the frozen period owner, else the assignee. */
export function periodOwnerOf(occurrence: Pick<BonusOccurrence, 'assigneeId' | 'periodOwnerId'>): string | null {
  return occurrence.periodOwnerId !== undefined ? occurrence.periodOwnerId : occurrence.assigneeId;
}

/** The person who receives the points of done work: `completedBy`, else the assignee (the rule of the execution points). */
export function creditedOf(occurrence: Pick<BonusOccurrence, 'status' | 'assigneeId' | 'completedBy'>): string | null {
  return occurrence.status === 'done' ? (occurrence.completedBy ?? occurrence.assigneeId) : null;
}

/** One person's view of an occurrence: the item that goes into that person's set. */
export interface Placement {
  person: string;
  item: BonusOccurrence;
}

/**
 * Where an occurrence counts (ADR-0012). Recorded work belongs to the credited person and never
 * blocks. Planned work belongs to its period owner. Open and skipped work is the owner's open item;
 * unassigned work is in nobody's set. Done work is the owner's done item when the owner is the
 * credited person. When somebody else did it, it is not done for the owner (it still blocks), and
 * for the person who did it it is non-blocking, like recorded work: never needed, never late.
 */
export function placementsOf(occurrence: BonusOccurrence): Placement[] {
  const credited = creditedOf(occurrence);
  if (occurrence.recordedDone === true) return credited === null ? [] : [{ person: credited, item: occurrence }];
  const owner = periodOwnerOf(occurrence);
  if (occurrence.status !== 'done') return owner === null ? [] : [{ person: owner, item: occurrence }];
  if (owner !== null && credited === owner) return [{ person: owner, item: occurrence }];
  const placements: Placement[] = [];
  if (owner !== null) placements.push({ person: owner, item: { ...occurrence, status: 'open', completedAt: null, completedBy: null } });
  if (credited !== null) placements.push({ person: credited, item: { ...occurrence, recordedDone: true, date: occurrence.plannedDate } });
  return placements;
}

export interface SetEvaluation {
  total: number;
  /** Occurrences that were planned; recorded work does not count here. */
  planned: number;
  done: number;
  open: number;
  skipped: number;
  /** At least one planned occurrence is needed to earn anything. */
  eligible: boolean;
  /** Every occurrence in the set is done, whenever it was completed. */
  allDone: boolean;
  /** Everything is done and every completion lies before the cut-off; planned done work without `completedAt` is late. Recorded work is always on time. */
  allOnTime: boolean;
}

/** Evaluates the set of one person for one period against the on-time cut-off. */
export function evaluateSet(set: readonly BonusOccurrence[], cutoff: Date): SetEvaluation {
  let planned = 0;
  let done = 0;
  let open = 0;
  let skipped = 0;
  let onTime = 0;
  for (const occurrence of set) {
    if (occurrence.recordedDone === true) {
      // Recorded work never blocks and is always on time, whatever its completion instant says
      // (an administrator may have corrected the date to before it).
      done += 1;
      onTime += 1;
      continue;
    }
    planned += 1;
    if (occurrence.status === 'done') {
      done += 1;
      if (occurrence.completedAt !== null && Date.parse(occurrence.completedAt) < cutoff.getTime()) onTime += 1;
    } else if (occurrence.status === 'skipped') skipped += 1;
    else open += 1;
  }
  const total = set.length;
  const allDone = total > 0 && done === total;
  return { total, planned, done, open, skipped, eligible: planned > 0, allDone, allOnTime: allDone && onTime === total };
}

/** The amounts in force for a period ending on `day`: the last row on or before it; all 0 before the first row. */
export function bonusAmountsOn(schedule: readonly BonusScheduleRow[], day: DayKey): BonusAmounts {
  let found: BonusScheduleRow | null = null;
  for (const row of schedule) {
    if (row.from <= day && (found === null || row.from > found.from)) found = row;
  }
  return found ? pickAmounts(found) : { ...NO_BONUSES };
}

function pickAmounts(row: BonusAmounts): BonusAmounts {
  return { weekDone: row.weekDone, weekOnTime: row.weekOnTime, cycleDone: row.cycleDone, cycleOnTime: row.cycleOnTime };
}

export function sameBonusAmounts(a: BonusAmounts, b: BonusAmounts): boolean {
  return a.weekDone === b.weekDone && a.weekOnTime === b.weekOnTime && a.cycleDone === b.cycleDone && a.cycleOnTime === b.cycleOnTime;
}

/**
 * The schedule after an administrator sets `amounts` today: unchanged when they equal the row in
 * force today, otherwise a row from today that replaces a row that already starts today. A change
 * never alters an ended period, because a period ends after its last day (ADR-0012).
 */
export function scheduleWithAmounts(schedule: readonly BonusScheduleRow[], amounts: BonusAmounts, todayKey: DayKey): BonusScheduleRow[] {
  if (sameBonusAmounts(bonusAmountsOn(schedule, todayKey), amounts)) return [...schedule];
  return [...schedule.filter((row) => row.from !== todayKey), { from: todayKey, ...pickAmounts(amounts) }].sort((a, b) =>
    a.from < b.from ? -1 : a.from > b.from ? 1 : 0,
  );
}

export interface ExpectedBonusEntry {
  /** `<kind>:<personId>:<periodStart>`; a cycle is keyed by its first day, not its index. */
  key: string;
  kind: BonusKind;
  personId: string;
  amount: number;
  periodStart: DayKey;
  /** The last day of the period; the bonus is dated then. */
  periodEnd: DayKey;
}

export function bonusKey(kind: BonusKind, personId: string, periodStart: DayKey): string {
  return `${kind}:${personId}:${periodStart}`;
}

export interface BonusContext {
  anchor: DayKey;
  timezone: string;
  /** Today in the household timezone. */
  today: DayKey;
  schedule: readonly BonusScheduleRow[];
  /**
   * The boundary of the last statistics reset: a period that starts before this day is never
   * evaluated, because its history was (partly) purged and what remains cannot be trusted.
   */
  floor?: DayKey | undefined;
}

/**
 * The bonus entries that must exist: for every person and every ended week and cycle that holds
 * at least one of their occurrences, one entry per kind whose amount is above 0, whose set is
 * eligible and whose condition holds. Ordered by period end, period start, person and kind, so
 * the result is deterministic. Occurrences without an owner are in nobody's set.
 */
export function expectedBonusEntries(items: readonly BonusOccurrence[], context: BonusContext): ExpectedBonusEntry[] {
  const groups = new Map<string, { person: string; period: Period; set: BonusOccurrence[] }>();
  const add = (person: string, period: Period, item: BonusOccurrence) => {
    const id = `${period.unit}:${person}:${period.start}`;
    const group = groups.get(id);
    if (group) group.set.push(item);
    else groups.set(id, { person, period, set: [item] });
  };
  for (const occurrence of items) {
    for (const { person, item } of placementsOf(occurrence)) {
      const day = periodDayOf(item);
      add(person, weekOf(day), item);
      add(person, cycleOf(day, context.anchor), item);
    }
  }

  const entries: ExpectedBonusEntry[] = [];
  for (const { person, period, set } of groups.values()) {
    if (!periodEnded(period, context.today)) continue;
    if (context.floor !== undefined && period.start < context.floor) continue;
    const evaluation = evaluateSet(set, onTimeCutoff(period, context.timezone));
    if (!evaluation.eligible) continue;
    const amounts = bonusAmountsOn(context.schedule, period.end);
    const kinds: BonusKind[] =
      period.unit === 'week' ? ['bonus_week_done', 'bonus_week_ontime'] : ['bonus_cycle_done', 'bonus_cycle_ontime'];
    for (const kind of kinds) {
      const amount = amounts[AMOUNT_OF_KIND[kind]];
      const earned = kind.endsWith('_ontime') ? evaluation.allOnTime : evaluation.allDone;
      if (amount > 0 && earned) {
        entries.push({ key: bonusKey(kind, person, period.start), kind, personId: person, amount, periodStart: period.start, periodEnd: period.end });
      }
    }
  }
  return entries.sort((a, b) =>
    a.periodEnd !== b.periodEnd ? (a.periodEnd < b.periodEnd ? -1 : 1) : a.key < b.key ? -1 : a.key > b.key ? 1 : 0,
  );
}
