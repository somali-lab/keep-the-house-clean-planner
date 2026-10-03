/**
 * Golden vectors (docs/plans/dotnet-rewrite.md section 7.2).
 *
 * An explicit table of inputs per function; the expected outputs are computed by the
 * TypeScript implementation. To add a module (bonuses, points, badges, rewards, validation),
 * add a `ModuleSpec` to MODULES: nothing else in the exporter or the drift test changes.
 *
 * Conventions: day keys are 'YYYY-MM-DD', instants are ISO strings (always with a Z),
 * weekdays are 0=Sunday..6=Saturday unless the function says Monday-first. A call that
 * throws is recorded as `throws` (the error class name) instead of `expected`.
 */
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  assertValidAnchor,
  cycleEnd,
  cycleIndexFor,
  cycleStart,
  slotDate,
  weekIndexFor,
} from '../packages/shared/src/cycle.ts';
import {
  BADGE_IMAGE_TYPES,
  MAX_BADGE_DESCRIPTION_LENGTH,
  MAX_BADGE_IMAGE_BYTES,
  MAX_BADGE_NAME_LENGTH,
  MAX_BADGE_RULE_TASKS,
  MAX_BADGE_THRESHOLD,
  MAX_BADGES,
  MAX_ON_TIME_WEEKS_THRESHOLD,
  MIN_BADGE_NAME_LENGTH,
} from '../packages/shared/src/badges.ts';
import { MAX_BONUS_POINTS, MIN_BONUS_POINTS } from '../packages/shared/src/bonuses.ts';
import { computeDue, dueState, type DueTaskInput } from '../packages/shared/src/due.ts';
import {
  DEFAULT_CURRENCY_CODE,
  defaultPointsForDuration,
  MAX_CENTS_PER_POINT,
  MAX_REDEMPTION_NOTE_LENGTH,
  MAX_TASK_POINTS,
  MIN_CENTS_PER_POINT,
  MIN_TASK_POINTS,
} from '../packages/shared/src/points.ts';
import {
  MAX_REWARD_GOAL_POINTS,
  MIN_REWARD_GOAL_POINTS,
  REWARD_EGG_COUNT,
} from '../packages/shared/src/rewards.ts';
import { CYCLE_DAYS, CYCLE_WEEKS } from '../packages/shared/src/cycle.ts';
import {
  MAX_POINTS_CORRECTIONS,
  MAX_POINTS_ENTRIES_RANGE_DAYS,
} from '../packages/shared/src/schemas/points.ts';
import {
  DEFAULT_AI_TIMEOUT_SECONDS,
  MAX_AI_TIMEOUT_SECONDS,
  MIN_AI_TIMEOUT_SECONDS,
} from '../packages/shared/src/schemas/settings.ts';
import { MAX_BROWSER_NOTIFICATION_TIMES } from '../packages/shared/src/schemas/users.ts';
import { PLAN_WEEKS } from '../packages/shared/src/validation/plan.ts';
import { DEFAULT_INTERVALS, type Interval } from '../packages/shared/src/schemas/intervals.ts';
import {
  addDays,
  daysBetween,
  fromDayKey,
  fromDayKeyTime,
  isDayKey,
  isMonday,
  isTimeOfDay,
  isWeekend,
  isoWeek,
  isoWeekLabel,
  mon0ToSun0,
  mondayOf,
  mondayOfIsoWeek,
  sun0ToMon0,
  toDayKey,
  today,
  weekdayMon0,
  weekdaySun0,
} from '../packages/shared/src/time.ts';

/** Where the C# tests read the vectors from. */
export const VECTORS_DIR = join(
  dirname(fileURLToPath(import.meta.url)),
  '..',
  'apps',
  'api',
  'tests',
  'Huishoudplanner.Domain.Tests',
  'Vectors',
);

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
type Input = { [key: string]: Json };

export interface VectorCase {
  name: string;
  function: string;
  input: Input;
  expected?: Json;
  throws?: string;
}

export interface VectorFile {
  module: string;
  cases: VectorCase[];
}

interface FunctionSpec {
  /** Calls the real function with a JSON input and returns a JSON-serialisable result. */
  call: (input: never) => Json;
  /** [case name, input] pairs. */
  cases: [string, Input][];
}

interface ModuleSpec {
  module: string;
  functions: Record<string, FunctionSpec>;
}

const TZ = 'Europe/Amsterdam';
const iso = (d: Date): string => d.toISOString();
/** Typed adapter so each `call` gets a real input type while the table stays JSON. */
const fn = <I extends Input>(call: (input: I) => Json, cases: [string, I][]): FunctionSpec => ({
  call: call as (input: never) => Json,
  cases,
});

// ---------------------------------------------------------------------------------------
// time
// ---------------------------------------------------------------------------------------

const keyOnly = (f: (key: string) => Json) => (i: { key: string }) => f(i.key);
const keyCases = (...keys: [string, string][]): [string, { key: string }][] =>
  keys.map(([name, key]) => [name, { key }]);
const weekdayCases = [0, 1, 2, 3, 4, 5, 6].map((weekday): [string, { weekday: number }] => [
  `${weekday}`,
  { weekday },
]);

const timeModule: ModuleSpec = {
  module: 'time',
  functions: {
    isDayKey: fn(
      keyOnly(isDayKey),
      keyCases(
        ['valid ordinary date', '2026-09-14'],
        ['valid leap day', '2028-02-29'],
        ['invalid: 29 Feb in a non-leap year', '2026-02-29'],
        ['invalid: century is not a leap year', '2100-02-29'],
        ['valid: 400-year rule makes 2000 a leap year', '2000-02-29'],
        ['invalid: 30 Feb', '2026-02-30'],
        ['invalid: month 13', '2026-13-01'],
        ['invalid: day 00', '2026-09-00'],
        ['invalid: unpadded month', '2026-9-14'],
        ['invalid: empty', ''],
        ['invalid: text', 'nope'],
        ['invalid: trailing time', '2026-09-14T00:00'],
      ),
    ),
    today: fn(
      (i: { tz: string; now: string }) => today(i.tz, new Date(i.now)),
      [
        ['Amsterdam is ahead of UTC on a summer evening', { tz: TZ, now: '2026-09-13T22:30:00Z' }],
        ['UTC keeps the previous day', { tz: 'UTC', now: '2026-09-13T22:30:00Z' }],
        [
          'winter evening: 23:30 UTC is already the next day',
          { tz: TZ, now: '2026-12-15T23:30:00Z' },
        ],
        ['winter: 22:59 UTC is still the same day', { tz: TZ, now: '2026-12-15T22:59:59Z' }],
        ['year boundary', { tz: TZ, now: '2026-12-31T23:00:00Z' }],
        ['leap day in Amsterdam', { tz: TZ, now: '2028-02-28T23:00:00Z' }],
      ],
    ),
    toDayKey: fn(
      (i: { instant: string; tz: string }) => toDayKey(new Date(i.instant), i.tz),
      [
        [
          'spring forward: last second of the 23-hour day',
          { instant: '2026-03-29T21:59:59Z', tz: TZ },
        ],
        [
          'spring forward: first second of the next day',
          { instant: '2026-03-29T22:00:00Z', tz: TZ },
        ],
        [
          'spring forward: first instant of the 23-hour day',
          { instant: '2026-03-28T23:00:00Z', tz: TZ },
        ],
        ['fall back: last second of the 25-hour day', { instant: '2026-10-25T22:59:59Z', tz: TZ }],
        ['fall back: first second of the next day', { instant: '2026-10-25T23:00:00Z', tz: TZ }],
        [
          'fall back: first instant of the 25-hour day',
          { instant: '2026-10-24T22:00:00Z', tz: TZ },
        ],
        ['fall back: the repeated hour (first 02:30)', { instant: '2026-10-25T00:30:00Z', tz: TZ }],
        [
          'fall back: the repeated hour (second 02:30)',
          { instant: '2026-10-25T01:30:00Z', tz: TZ },
        ],
        ['leap day', { instant: '2028-02-29T12:00:00Z', tz: TZ }],
        ['UTC midnight', { instant: '2026-09-14T00:00:00Z', tz: 'UTC' }],
        ['invalid timezone throws', { instant: '2026-09-14T00:00:00Z', tz: 'Not/AZone' }],
      ],
    ),
    fromDayKey: fn(
      (i: { key: string; tz: string }) => iso(fromDayKey(i.key, i.tz)),
      [
        ['ordinary summer day', { key: '2026-09-14', tz: TZ }],
        ['ordinary winter day', { key: '2026-12-16', tz: TZ }],
        ['spring forward day starts in CET', { key: '2026-03-29', tz: TZ }],
        ['day after spring forward starts in CEST', { key: '2026-03-30', tz: TZ }],
        ['fall back day starts in CEST', { key: '2026-10-25', tz: TZ }],
        ['day after fall back starts in CET', { key: '2026-10-26', tz: TZ }],
        ['leap day', { key: '2028-02-29', tz: TZ }],
        ['UTC', { key: '2026-09-14', tz: 'UTC' }],
        ['invalid key throws', { key: 'nope', tz: TZ }],
        ['non-existent date throws', { key: '2026-02-30', tz: TZ }],
        ['invalid timezone throws', { key: '2026-09-14', tz: 'Not/AZone' }],
      ],
    ),
    isTimeOfDay: fn(
      (i: { value: string }) => isTimeOfDay(i.value),
      ['00:00', '07:30', '23:59', '24:00', '7:30', '07:60', '0730', ''].map(
        (value): [string, { value: string }] => [`'${value}'`, { value }],
      ),
    ),
    fromDayKeyTime: fn(
      (i: { key: string; time: string; tz: string }) => iso(fromDayKeyTime(i.key, i.time, i.tz)),
      [
        ['summer time in Amsterdam', { key: '2026-09-16', time: '10:00', tz: TZ }],
        ['winter time in Amsterdam', { key: '2026-12-16', time: '10:00', tz: TZ }],
        ['UTC', { key: '2026-09-16', time: '10:00', tz: 'UTC' }],
        ['spring forward day, 08:00 is summer time', { key: '2026-03-29', time: '08:00', tz: TZ }],
        [
          'spring forward day, 01:30 is still winter time',
          { key: '2026-03-29', time: '01:30', tz: TZ },
        ],
        ['spring forward gap: 02:00 is skipped', { key: '2026-03-29', time: '02:00', tz: TZ }],
        [
          'spring forward gap: 02:30 lands after the gap',
          { key: '2026-03-29', time: '02:30', tz: TZ },
        ],
        ['spring forward gap: 02:59', { key: '2026-03-29', time: '02:59', tz: TZ }],
        ['spring forward: 03:00 exists', { key: '2026-03-29', time: '03:00', tz: TZ }],
        ['fall back day, 08:00 is winter time', { key: '2026-10-25', time: '08:00', tz: TZ }],
        [
          'fall back overlap: 02:00 is the first occurrence',
          { key: '2026-10-25', time: '02:00', tz: TZ },
        ],
        [
          'fall back overlap: 02:30 is the first occurrence',
          { key: '2026-10-25', time: '02:30', tz: TZ },
        ],
        [
          'fall back overlap: 02:59 is the first occurrence',
          { key: '2026-10-25', time: '02:59', tz: TZ },
        ],
        ['fall back: 03:00 is winter time', { key: '2026-10-25', time: '03:00', tz: TZ }],
        ['fall back: 01:59 is summer time', { key: '2026-10-25', time: '01:59', tz: TZ }],
        ['leap day', { key: '2028-02-29', time: '23:59', tz: TZ }],
        ['midnight', { key: '2026-09-16', time: '00:00', tz: TZ }],
        ['invalid key throws', { key: 'nope', time: '10:00', tz: TZ }],
        ['invalid time throws', { key: '2026-09-16', time: '25:00', tz: TZ }],
        ['invalid timezone throws', { key: '2026-09-16', time: '10:00', tz: 'Not/AZone' }],
      ],
    ),
    addDays: fn(
      (i: { key: string; days: number }) => addDays(i.key, i.days),
      [
        ['one day', { key: '2026-09-14', days: 1 }],
        ['zero days', { key: '2026-09-14', days: 0 }],
        ['negative', { key: '2026-09-14', days: -14 }],
        ['into the spring forward day', { key: '2026-03-28', days: 1 }],
        ['across the spring forward day', { key: '2026-03-28', days: 2 }],
        ['into the fall back day', { key: '2026-10-24', days: 1 }],
        ['across the fall back day', { key: '2026-10-24', days: 3 }],
        ['month end', { key: '2026-01-31', days: 1 }],
        ['year end', { key: '2026-12-31', days: 1 }],
        ['year start backwards', { key: '2027-01-01', days: -1 }],
        ['leap day forwards', { key: '2028-02-28', days: 1 }],
        ['leap day skipped in a non-leap year', { key: '2027-02-28', days: 1 }],
        ['over a leap day', { key: '2028-02-28', days: 2 }],
        ['a full 28-day cycle across fall back', { key: '2026-10-12', days: 28 }],
        ['invalid key throws', { key: '2026-13-01', days: 1 }],
        ['non-day-key text throws', { key: 'nope', days: 1 }],
      ],
    ),
    daysBetween: fn(
      (i: { from: string; to: string }) => daysBetween(i.from, i.to),
      [
        ['same day', { from: '2026-09-14', to: '2026-09-14' }],
        ['one week', { from: '2026-09-14', to: '2026-09-21' }],
        ['negative', { from: '2026-09-21', to: '2026-09-14' }],
        ['across spring forward (23-hour day)', { from: '2026-03-28', to: '2026-03-30' }],
        ['across fall back (25-hour day)', { from: '2026-10-24', to: '2026-10-27' }],
        ['spring forward day to next', { from: '2026-03-29', to: '2026-03-30' }],
        ['fall back day to next', { from: '2026-10-25', to: '2026-10-26' }],
        ['across a whole DST half year', { from: '2026-03-01', to: '2026-11-01' }],
        ['across a leap day', { from: '2028-02-28', to: '2028-03-01' }],
        ['no leap day in a non-leap year', { from: '2027-02-28', to: '2027-03-01' }],
        ['a leap year', { from: '2028-01-01', to: '2029-01-01' }],
        ['a non-leap year', { from: '2026-01-01', to: '2027-01-01' }],
        ['invalid from throws', { from: 'nope', to: '2026-09-14' }],
        ['invalid to throws', { from: '2026-09-14', to: '2026-02-30' }],
      ],
    ),
    weekdaySun0: fn(
      keyOnly(weekdaySun0),
      keyCases(
        ['Sunday', '2026-09-13'],
        ['Monday', '2026-09-14'],
        ['Saturday', '2026-09-19'],
        ['leap day (Tuesday)', '2028-02-29'],
        ['spring forward day (Sunday)', '2026-03-29'],
        ['fall back day (Sunday)', '2026-10-25'],
        ['invalid throws', 'nope'],
      ),
    ),
    weekdayMon0: fn(
      keyOnly(weekdayMon0),
      keyCases(
        ['Sunday', '2026-09-13'],
        ['Monday', '2026-09-14'],
        ['Saturday', '2026-09-19'],
        ['leap day (Tuesday)', '2028-02-29'],
        ['invalid throws', 'nope'],
      ),
    ),
    sun0ToMon0: fn((i: { weekday: number }) => sun0ToMon0(i.weekday), weekdayCases),
    mon0ToSun0: fn((i: { weekday: number }) => mon0ToSun0(i.weekday), weekdayCases),
    isMonday: fn(
      keyOnly(isMonday),
      keyCases(
        ['Monday', '2026-09-14'],
        ['Sunday', '2026-09-13'],
        ['Tuesday', '2026-09-15'],
        ['invalid text is not a Monday and does not throw', 'nope'],
        ['non-existent date is not a Monday and does not throw', '2026-02-30'],
      ),
    ),
    isWeekend: fn(
      keyOnly(isWeekend),
      keyCases(
        ['Friday', '2026-09-18'],
        ['Saturday', '2026-09-19'],
        ['Sunday', '2026-09-20'],
        ['Monday', '2026-09-21'],
        ['invalid throws', 'nope'],
      ),
    ),
    isoWeek: fn(
      keyOnly((key) => ({ ...isoWeek(key) })),
      keyCases(
        ['ordinary week', '2026-09-14'],
        ['week 53 ends the year', '2026-12-31'],
        ['early January still in week 53 of the previous year', '2027-01-03'],
        ['first Monday of 2027 is week 1', '2027-01-04'],
        ['late December already in week 1 of the next year', '2025-12-29'],
        ['1 Jan 2021 belongs to 2020-W53', '2021-01-01'],
        ['1 Jan 2026 is week 1', '2026-01-01'],
        ['1 Jan 2027 belongs to 2026-W53', '2027-01-01'],
        ['31 Dec 2024 belongs to 2025-W01', '2024-12-31'],
        ['leap day', '2028-02-29'],
        ['spring forward day', '2026-03-29'],
        ['fall back day', '2026-10-25'],
        ['invalid throws', 'nope'],
      ),
    ),
    isoWeekLabel: fn(
      keyOnly(isoWeekLabel),
      keyCases(
        ['ordinary week', '2026-09-14'],
        ['zero-padded single digit week', '2026-01-05'],
        ['Sunday ends the week', '2026-09-20'],
        ['2026-W53 Thursday', '2026-12-31'],
        ['2026-W53 Sunday', '2027-01-03'],
        ['2027-W01 Monday', '2027-01-04'],
        ['2026-W01 starts in December 2025', '2025-12-29'],
        ['2020-W53', '2021-01-01'],
        ['2021-W01', '2021-01-04'],
        ['2025-W01 starts in December 2024', '2024-12-30'],
        ['2024-W01 starts on 1 Jan', '2024-01-01'],
        ['2016 ends in W52', '2017-01-01'],
        ['invalid throws', 'nope'],
      ),
    ),
    mondayOfIsoWeek: fn(
      (i: { label: string }) => mondayOfIsoWeek(i.label),
      [
        ['ordinary week', { label: '2026-W38' }],
        ['week 1 starting in the previous year', { label: '2026-W01' }],
        ['week 1 of 2025 starts in December 2024', { label: '2025-W01' }],
        ['week 53 exists in 2026', { label: '2026-W53' }],
        ['week 53 exists in 2020', { label: '2020-W53' }],
        ['week 53 does not exist in 2027', { label: '2027-W53' }],
        ['week 52 of 2027', { label: '2027-W52' }],
        ['week 00 does not exist', { label: '2026-W00' }],
        ['week 54 does not exist', { label: '2026-W54' }],
        ['missing W', { label: '2026-38' }],
        ['single digit week', { label: '2026-W3' }],
        ['lowercase w', { label: '2026-w38' }],
        ['empty', { label: '' }],
        ['leading space', { label: ' 2026-W38' }],
      ],
    ),
    mondayOf: fn(
      keyOnly(mondayOf),
      keyCases(
        ['Sunday goes back six days', '2026-09-13'],
        ['Monday is itself', '2026-09-14'],
        ['Wednesday', '2026-09-16'],
        ['across month boundary', '2026-10-01'],
        ['across year boundary', '2027-01-01'],
        ['spring forward Sunday', '2026-03-29'],
        ['fall back Sunday', '2026-10-25'],
        ['leap day', '2028-02-29'],
        ['invalid throws', 'nope'],
      ),
    ),
  },
};

// ---------------------------------------------------------------------------------------
// cycle
// ---------------------------------------------------------------------------------------

const ANCHOR = '2026-09-14'; // Monday

interface SlotInput {
  [key: string]: Json;
  cycleStartDate: string;
  weekIndex: number;
  weekday: number;
}
const slotGrid = [0, 1, 2, 3].flatMap((weekIndex) =>
  [0, 1, 2, 3, 4, 5, 6].map((weekday): [string, SlotInput] => [
    `cycle 2: weekIndex ${weekIndex}, weekday ${weekday}`,
    { cycleStartDate: cycleStart(2, ANCHOR), weekIndex, weekday },
  ]),
);

const cycleModule: ModuleSpec = {
  module: 'cycle',
  functions: {
    assertValidAnchor: fn(
      (i: { anchor: string }) => {
        assertValidAnchor(i.anchor);
        return null;
      },
      [
        ['a Monday is accepted', { anchor: ANCHOR }],
        ['a Sunday throws', { anchor: '2026-09-13' }],
        ['a Tuesday throws', { anchor: '2026-09-15' }],
        ['not a date throws', { anchor: 'not-a-date' }],
        ['a Monday next to a leap day is accepted', { anchor: '2028-02-28' }],
      ],
    ),
    cycleIndexFor: fn(
      (i: { date: string; anchor: string }) => cycleIndexFor(i.date, i.anchor),
      [
        ['the anchor itself', { date: ANCHOR, anchor: ANCHOR }],
        ['last day of cycle 0', { date: '2026-10-11', anchor: ANCHOR }],
        ['first day of cycle 1', { date: '2026-10-12', anchor: ANCHOR }],
        ['day before the anchor is cycle -1', { date: '2026-09-13', anchor: ANCHOR }],
        ['first day of cycle -1', { date: '2026-08-17', anchor: ANCHOR }],
        ['day before cycle -1 is cycle -2', { date: '2026-08-16', anchor: ANCHOR }],
        ['inside the fall back day', { date: '2026-10-25', anchor: ANCHOR }],
        ['the Monday after fall back', { date: '2026-10-26', anchor: ANCHOR }],
        ['spring forward day, several cycles later', { date: '2027-03-28', anchor: ANCHOR }],
        ['the day after spring forward', { date: '2027-03-29', anchor: ANCHOR }],
        ['across a leap day', { date: '2028-03-01', anchor: ANCHOR }],
        ['across a year boundary', { date: '2027-01-01', anchor: ANCHOR }],
        ['spring forward day inside cycle 0', { date: '2026-03-29', anchor: '2026-03-16' }],
        ['anchor that is not a Monday throws', { date: '2026-09-20', anchor: '2026-09-15' }],
        ['invalid date throws', { date: 'nope', anchor: ANCHOR }],
      ],
    ),
    cycleStart: fn(
      (i: { index: number; anchor: string }) => cycleStart(i.index, i.anchor),
      [
        ['cycle 0 is the anchor', { index: 0, anchor: ANCHOR }],
        ['cycle 1', { index: 1, anchor: ANCHOR }],
        ['cycle -1', { index: -1, anchor: ANCHOR }],
        ['cycle 2', { index: 2, anchor: ANCHOR }],
        ['cycle 7 (contains spring forward 2027)', { index: 7, anchor: ANCHOR }],
        ['cycle across a year boundary', { index: 4, anchor: ANCHOR }],
        ['cycle across a leap day', { index: 20, anchor: ANCHOR }],
        ['non-Monday anchor throws', { index: 0, anchor: '2026-09-13' }],
      ],
    ),
    cycleEnd: fn(
      (i: { index: number; anchor: string }) => cycleEnd(i.index, i.anchor),
      [
        ['cycle 0 ends on a Sunday', { index: 0, anchor: ANCHOR }],
        ['cycle 1 ends after the fall back day', { index: 1, anchor: ANCHOR }],
        ['cycle -1', { index: -1, anchor: ANCHOR }],
        ['cycle across a leap day', { index: 20, anchor: ANCHOR }],
        ['non-Monday anchor throws', { index: 0, anchor: '2026-09-13' }],
      ],
    ),
    weekIndexFor: fn(
      (i: { date: string; anchor: string }) => weekIndexFor(i.date, i.anchor),
      [
        ['the anchor itself', { date: ANCHOR, anchor: ANCHOR }],
        ['end of week 0', { date: '2026-09-20', anchor: ANCHOR }],
        ['start of week 1', { date: '2026-09-21', anchor: ANCHOR }],
        ['end of cycle is week 3', { date: '2026-10-11', anchor: ANCHOR }],
        ['start of week 0 in the next cycle', { date: '2026-10-12', anchor: ANCHOR }],
        ['day before the anchor is week 3 of cycle -1', { date: '2026-09-13', anchor: ANCHOR }],
        ['day 8 before the anchor (negative cycle)', { date: '2026-09-06', anchor: ANCHOR }],
        ['first day of cycle -1 is week 0', { date: '2026-08-17', anchor: ANCHOR }],
        ['fall back day (week 1 of cycle 1)', { date: '2026-10-25', anchor: ANCHOR }],
        ['Monday after fall back', { date: '2026-10-26', anchor: ANCHOR }],
        ['spring forward day', { date: '2027-03-28', anchor: ANCHOR }],
        ['across a leap day', { date: '2028-02-29', anchor: ANCHOR }],
        ['non-Monday anchor throws', { date: ANCHOR, anchor: '2026-09-15' }],
      ],
    ),
    slotDate: fn(
      (i: SlotInput) => slotDate(i.cycleStartDate, i.weekIndex, i.weekday),
      [
        ...slotGrid,
        ['Monday of the first week', { cycleStartDate: ANCHOR, weekIndex: 0, weekday: 1 }],
        [
          'Sunday is the end of the first week',
          { cycleStartDate: ANCHOR, weekIndex: 0, weekday: 0 },
        ],
        ['Saturday of the last week', { cycleStartDate: ANCHOR, weekIndex: 3, weekday: 6 }],
        [
          'Sunday of the last week is the cycle end',
          { cycleStartDate: ANCHOR, weekIndex: 3, weekday: 0 },
        ],
        [
          'fall back cycle, Sunday week 0',
          { cycleStartDate: '2026-10-12', weekIndex: 0, weekday: 0 },
        ],
        [
          'fall back cycle, Sunday week 1 is the fall back day',
          { cycleStartDate: '2026-10-12', weekIndex: 1, weekday: 0 },
        ],
        [
          'fall back cycle, Sunday week 2',
          { cycleStartDate: '2026-10-12', weekIndex: 2, weekday: 0 },
        ],
        [
          'fall back cycle, Sunday week 3',
          { cycleStartDate: '2026-10-12', weekIndex: 3, weekday: 0 },
        ],
        [
          'cycle with the spring forward day',
          { cycleStartDate: '2027-03-15', weekIndex: 1, weekday: 0 },
        ],
        ['cycle with a leap day', { cycleStartDate: '2028-02-14', weekIndex: 2, weekday: 2 }],
        ['weekIndex 4 throws', { cycleStartDate: ANCHOR, weekIndex: 4, weekday: 0 }],
        ['weekday 7 throws', { cycleStartDate: ANCHOR, weekIndex: 0, weekday: 7 }],
        ['negative weekIndex throws', { cycleStartDate: ANCHOR, weekIndex: -1, weekday: 0 }],
        ['negative weekday throws', { cycleStartDate: ANCHOR, weekIndex: 0, weekday: -1 }],
        ['fractional weekIndex throws', { cycleStartDate: ANCHOR, weekIndex: 1.5, weekday: 0 }],
        ['fractional weekday throws', { cycleStartDate: ANCHOR, weekIndex: 0, weekday: 1.5 }],
      ],
    ),
  },
};

// ---------------------------------------------------------------------------------------
// due
// ---------------------------------------------------------------------------------------

type DueInputTask = Omit<DueTaskInput, 'lastCompletedAt'> & { lastCompletedAt: string | null };
interface ComputeDueInput {
  [key: string]: Json;
  tasks: DueInputTask[];
  intervals: Interval[];
  today: string;
  timezone: string;
}

const dueTask = (_id: string, overrides: Partial<DueInputTask> = {}): DueInputTask => ({
  _id,
  active: true,
  intervalKey: '1w',
  lastCompletedAt: null,
  initialDueDate: '2026-09-16',
  ...overrides,
});
const due = (tasks: DueInputTask[], todayKey: string, timezone = TZ): ComputeDueInput => ({
  tasks,
  intervals: DEFAULT_INTERVALS.map((i) => ({ ...i })),
  today: todayKey,
  timezone,
});

const dueModule: ModuleSpec = {
  module: 'due',
  functions: {
    dueState: fn(
      (i: { ratio: number }) => dueState(i.ratio),
      [0, 0.5, 0.99, 1.0, 1.49, 1.5, 12].map((ratio): [string, { ratio: number }] => [
        `ratio ${ratio}`,
        { ratio },
      ]),
    ),
    computeDue: fn(
      (i: ComputeDueInput) =>
        computeDue(i.tasks, i.intervals, i.today, i.timezone) as unknown as Json,
      [
        [
          'hits the exact boundaries 1.0 and 1.5',
          due(
            [
              dueTask('six', { lastCompletedAt: '2026-09-10T10:00:00Z' }),
              dueTask('seven', { lastCompletedAt: '2026-09-09T10:00:00Z' }),
              dueTask('fortnight-20', {
                intervalKey: '2wk',
                lastCompletedAt: '2026-08-27T10:00:00Z',
              }),
              dueTask('fortnight-21', {
                intervalKey: '2wk',
                lastCompletedAt: '2026-08-26T10:00:00Z',
              }),
            ],
            '2026-09-16',
          ),
        ],
        [
          'counts local calendar days across fall back (49.5 hours is 2 days)',
          due(
            [dueTask('fall', { intervalKey: 'daily', lastCompletedAt: '2026-10-24T20:30:00Z' })],
            '2026-10-26',
          ),
        ],
        [
          'counts local calendar days across spring forward',
          due(
            [dueTask('spring', { intervalKey: 'daily', lastCompletedAt: '2026-03-28T23:30:00Z' })],
            '2026-03-30',
          ),
        ],
        [
          'completion at 23:30 UTC on the eve of spring forward is local 00:30 CET',
          due(
            [
              dueTask('spring-eve', {
                intervalKey: 'daily',
                lastCompletedAt: '2026-03-28T23:30:00Z',
              }),
            ],
            '2026-03-29',
          ),
        ],
        [
          'completion in the repeated fall back hour (second 02:30)',
          due(
            [dueTask('repeat', { intervalKey: 'daily', lastCompletedAt: '2026-10-25T01:30:00Z' })],
            '2026-10-26',
          ),
        ],
        [
          'completion in the last second of the 25-hour day',
          due(
            [dueTask('last', { intervalKey: 'daily', lastCompletedAt: '2026-10-25T22:59:59Z' })],
            '2026-10-26',
          ),
        ],
        [
          'completion in the first second after the 25-hour day',
          due(
            [dueTask('next', { intervalKey: 'daily', lastCompletedAt: '2026-10-25T23:00:00Z' })],
            '2026-10-26',
          ),
        ],
        [
          'uses the local day, not the UTC day, of the last completion',
          due(
            [dueTask('late', { intervalKey: 'daily', lastCompletedAt: '2026-09-15T23:30:00Z' })],
            '2026-09-16',
          ),
        ],
        [
          'the same completion read in UTC counts the UTC day',
          due(
            [dueTask('late', { intervalKey: 'daily', lastCompletedAt: '2026-09-15T23:30:00Z' })],
            '2026-09-16',
            'UTC',
          ),
        ],
        [
          'completion with an offset instead of Z',
          due(
            [
              dueTask('offset', {
                intervalKey: 'daily',
                lastCompletedAt: '2026-09-15T23:30:00+00:00',
              }),
            ],
            '2026-09-16',
          ),
        ],
        [
          'before the initial due date a never-completed task is not due',
          due(
            [dueTask('new', { intervalKey: 'quarter', initialDueDate: '2026-10-16' })],
            '2026-09-20',
          ),
        ],
        [
          'on the initial due date a never-completed task is due after one period',
          due(
            [dueTask('new', { intervalKey: 'quarter', initialDueDate: '2026-10-16' })],
            '2026-10-16',
          ),
        ],
        [
          'a never-completed task grows past one period after its initial due date',
          due([dueTask('new', { intervalKey: '1w', initialDueDate: '2026-09-09' })], '2026-09-16'),
        ],
        [
          'a never-completed task reaches overdue half a period later',
          due(
            [dueTask('new', { intervalKey: 'quarter', initialDueDate: '2026-10-16' })],
            '2026-12-01',
          ),
        ],
        [
          'a never-completed task counts days across DST',
          due([dueTask('new', { intervalKey: '4wk', initialDueDate: '2026-10-12' })], '2026-11-09'),
        ],
        [
          'ranks by ratio, skips inactive tasks and unknown intervals',
          due(
            [
              dueTask('a', { lastCompletedAt: '2026-09-12T10:00:00Z' }),
              dueTask('b', { intervalKey: 'daily', lastCompletedAt: '2026-09-13T10:00:00Z' }),
              dueTask('c', { active: false, lastCompletedAt: '2025-01-01T10:00:00Z' }),
              dueTask('d', { intervalKey: 'mystery' }),
              dueTask('e', { intervalKey: '2wk', lastCompletedAt: '2026-09-02T10:00:00Z' }),
            ],
            '2026-09-16',
          ),
        ],
        [
          'equal ratio and days since are ordered by task id',
          due(
            [
              dueTask('z', { lastCompletedAt: '2026-09-09T10:00:00Z' }),
              dueTask('b', { lastCompletedAt: '2026-09-09T10:00:00Z' }),
              dueTask('m', { lastCompletedAt: '2026-09-09T10:00:00Z' }),
            ],
            '2026-09-16',
          ),
        ],
        [
          'equal ratio but different days since: more days ranks first',
          due(
            [
              dueTask('few', { intervalKey: '1w', lastCompletedAt: '2026-09-09T10:00:00Z' }),
              dueTask('many', { intervalKey: '2wk', lastCompletedAt: '2026-09-02T10:00:00Z' }),
            ],
            '2026-09-16',
          ),
        ],
        [
          'never reports negative days for a completion in the future',
          due([dueTask('x', { lastCompletedAt: '2026-09-20T10:00:00Z' })], '2026-09-16'),
        ],
        ['no tasks', due([], '2026-09-16')],
        [
          'only inactive tasks',
          due(
            [dueTask('off', { active: false, lastCompletedAt: '2026-09-01T10:00:00Z' })],
            '2026-09-16',
          ),
        ],
        [
          'completed today',
          due(
            [dueTask('now', { intervalKey: 'daily', lastCompletedAt: '2026-09-16T08:00:00Z' })],
            '2026-09-16',
          ),
        ],
        [
          'completion across a leap day',
          due(
            [dueTask('leap', { intervalKey: '4wk', lastCompletedAt: '2028-02-10T10:00:00Z' })],
            '2028-03-07',
          ),
        ],
        [
          'completion across a year boundary',
          due(
            [dueTask('ny', { intervalKey: '2wk', lastCompletedAt: '2026-12-25T10:00:00Z' })],
            '2027-01-08',
          ),
        ],
        [
          'a custom interval list is used instead of the defaults',
          {
            tasks: [
              dueTask('custom', {
                intervalKey: 'fortnight',
                lastCompletedAt: '2026-09-01T10:00:00Z',
              }),
            ],
            intervals: [{ key: 'fortnight', label: 'Fortnight', perCycle: 2, periodDays: 14 }],
            today: '2026-09-16',
            timezone: TZ,
          },
        ],
      ],
    ),
  },
};

// ---------------------------------------------------------------------------------------
// limits (GET /api/v2/meta/limits)
// ---------------------------------------------------------------------------------------

/**
 * Every limit and default of the API as one flat object keyed `group.name`, the same names the endpoint
 * publishes (the C# test flattens the JSON document of the endpoint and compares). Values with an exported
 * constant in packages/shared come from it; the ones that exist only inline in a zod schema are written here
 * with the schema they are copied from. `calendar.maxRangeDays` is new in v2 and has no TypeScript source:
 * it is pinned to the 53 weeks of MAX_POINTS_ENTRIES_RANGE_DAYS on purpose.
 */
const limitsDocument = (): Json => ({
  'calendar.cycleDays': CYCLE_DAYS,
  'calendar.cycleWeeks': CYCLE_WEEKS,
  'calendar.planWeeks': PLAN_WEEKS,
  'calendar.maxRangeDays': MAX_POINTS_ENTRIES_RANGE_DAYS,
  // schemas/occurrences.ts one-off name .max(120); schemas/intervals.ts key .max(32); schemas/occurrences.ts skip reason .max(500)
  'tasks.minPoints': MIN_TASK_POINTS,
  'tasks.maxPoints': MAX_TASK_POINTS,
  'tasks.minDurationMinutes': 1,
  'tasks.oneOffNameMaxLength': 120,
  'tasks.intervalKeyMaxLength': 32,
  'tasks.skipReasonMaxLength': 500,
  'points.minCentsPerPoint': MIN_CENTS_PER_POINT,
  'points.maxCentsPerPoint': MAX_CENTS_PER_POINT,
  'points.maxRedemptionNoteLength': MAX_REDEMPTION_NOTE_LENGTH,
  'points.maxEntriesRangeDays': MAX_POINTS_ENTRIES_RANGE_DAYS,
  'points.maxCorrections': MAX_POINTS_CORRECTIONS,
  'bonuses.minPoints': MIN_BONUS_POINTS,
  'bonuses.maxPoints': MAX_BONUS_POINTS,
  'rewards.minGoalPoints': MIN_REWARD_GOAL_POINTS,
  'rewards.maxGoalPoints': MAX_REWARD_GOAL_POINTS,
  'rewards.eggCount': REWARD_EGG_COUNT,
  'badges.minNameLength': MIN_BADGE_NAME_LENGTH,
  'badges.maxNameLength': MAX_BADGE_NAME_LENGTH,
  'badges.maxDescriptionLength': MAX_BADGE_DESCRIPTION_LENGTH,
  'badges.maxImageBytes': MAX_BADGE_IMAGE_BYTES,
  'badges.imageTypes': [...BADGE_IMAGE_TYPES],
  'badges.maxThreshold': MAX_BADGE_THRESHOLD,
  'badges.maxOnTimeWeeksThreshold': MAX_ON_TIME_WEEKS_THRESHOLD,
  'badges.maxBadges': MAX_BADGES,
  'badges.maxRuleTasks': MAX_BADGE_RULE_TASKS,
  'notifications.maxBrowserTimes': MAX_BROWSER_NOTIFICATION_TIMES,
  // schemas/ai.ts aiConstraintsSchema .max(2000); schemas/settings.ts aiPromptsSchema .max(8000), aiPromptTemplateSchema .max(20000)
  'ai.minTimeoutSeconds': MIN_AI_TIMEOUT_SECONDS,
  'ai.maxTimeoutSeconds': MAX_AI_TIMEOUT_SECONDS,
  'ai.constraintsMaxLength': 2000,
  'ai.promptMaxLength': 8000,
  'ai.promptTemplateMaxLength': 20000,
  // schemas/auditLog.ts limit .min(1).max(200).default(50); schemas/stats.ts cycles .min(1).max(26).default(4)
  'audit.defaultPageSize': 50,
  'audit.maxPageSize': 200,
  'statistics.defaultCycles': 4,
  'statistics.maxCycles': 26,
  'defaults.currencyCode': DEFAULT_CURRENCY_CODE,
  'defaults.aiTimeoutSeconds': DEFAULT_AI_TIMEOUT_SECONDS,
  'defaults.intervals': DEFAULT_INTERVALS.map((i) => ({ ...i })),
});

const limitsModule: ModuleSpec = {
  module: 'limits',
  functions: {
    limits: fn(() => limitsDocument(), [['all limits and defaults of the endpoint', {}]]),
    defaultPointsForDuration: fn(
      (i: { minutes: number }) => defaultPointsForDuration(i.minutes),
      [
        ['one point per minute', { minutes: 45 }],
        ['smallest duration', { minutes: 1 }],
        ['floor of one point', { minutes: 0 }],
        ['negative duration floors at one', { minutes: -5 }],
        ['exactly the maximum', { minutes: 1000 }],
        ['capped at the maximum', { minutes: 5000 }],
      ],
    ),
  },
};

export const MODULES: ModuleSpec[] = [timeModule, cycleModule, dueModule, limitsModule];

/** Runs every case through the real TypeScript function. */
export function buildVectors(): VectorFile[] {
  return MODULES.map(({ module, functions }) => ({
    module,
    cases: Object.entries(functions).flatMap(([name, spec]) =>
      spec.cases.map(([caseName, input]): VectorCase => {
        try {
          return { name: caseName, function: name, input, expected: spec.call(input as never) };
        } catch (error) {
          return {
            name: caseName,
            function: name,
            input,
            throws: error instanceof Error ? error.constructor.name : 'Error',
          };
        }
      }),
    ),
  }));
}

export function serializeVectors(file: VectorFile): string {
  return `${JSON.stringify(file, null, 2)}\n`;
}
