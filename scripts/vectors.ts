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
  EXAMPLE_BADGES,
  evaluateBadgeRule,
  ruleCovers,
  sniffBadgeImageType,
  MAX_BADGE_DESCRIPTION_LENGTH,
  MAX_BADGE_IMAGE_BYTES,
  MAX_BADGE_NAME_LENGTH,
  MAX_BADGE_RULE_TASKS,
  MAX_BADGE_THRESHOLD,
  MAX_BADGES,
  MAX_ON_TIME_WEEKS_THRESHOLD,
  MIN_BADGE_NAME_LENGTH,
} from '../packages/shared/src/badges.ts';
import {
  bonusAmountsOn,
  bonusKey,
  creditedOf,
  cycleOf,
  evaluateSet,
  expectedBonusEntries,
  isBonusKind,
  MAX_BONUS_POINTS,
  MIN_BONUS_POINTS,
  onTimeCutoff,
  periodDayOf,
  periodEnded,
  periodOwnerOf,
  placementsOf,
  sameBonusAmounts,
  scheduleWithAmounts,
  weekOf,
} from '../packages/shared/src/bonuses.ts';
import { computeDue, dueState, type DueTaskInput } from '../packages/shared/src/due.ts';
import {
  DEFAULT_CURRENCY_CODE,
  defaultPointsForDuration,
  isTwoDecimalCurrency,
  MAX_CENTS_PER_POINT,
  MAX_REDEMPTION_NOTE_LENGTH,
  MAX_TASK_POINTS,
  MIN_CENTS_PER_POINT,
  MIN_TASK_POINTS,
  pointsToCents,
} from '../packages/shared/src/points.ts';
import {
  automaticGoal,
  eggsForPercent,
  MAX_REWARD_GOAL_POINTS,
  MIN_REWARD_GOAL_POINTS,
  resolveRewardGoal,
  REWARD_EGG_COUNT,
  rewardPercent,
  sameRewardGoals,
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
import {
  budgetFor,
  isWeekendDay,
  PLAN_WEEKS,
  validatePlan,
  type PlanSlot,
  type PlanTask,
  type PlanUser,
  type ValidatePlanInput,
} from '../packages/shared/src/validation/plan.ts';
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
  /** Exported functions of the source module that have no vectors yet (their slice is not built); the drift test lists them. */
  pending?: string[];
  /** Exported functions that are deliberately not ported (they stay in the web app); the drift test lists them. */
  omitted?: string[];
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
// bonuses (packages/shared/src/bonuses.ts; scenarios of bonuses.test.ts plus edge cases)
// ---------------------------------------------------------------------------------------

/** Type aliases (not the interfaces of bonuses.ts): an alias has the implicit index signature that makes a case input assignable to Json. */
type BonusAmounts = {
  weekDone: number;
  weekOnTime: number;
  cycleDone: number;
  cycleOnTime: number;
};
type BonusScheduleRow = BonusAmounts & { from: string };

const amounts = (
  weekDone: number,
  weekOnTime: number,
  cycleDone: number,
  cycleOnTime: number,
): BonusAmounts => ({
  weekDone,
  weekOnTime,
  cycleDone,
  cycleOnTime,
});
const row = (from: string, a: BonusAmounts): BonusScheduleRow => ({ from, ...a });
const ALL_AMOUNTS = amounts(5, 3, 20, 10);
const OTHER_AMOUNTS = amounts(1, 2, 3, 4);
const ZERO_AMOUNTS = amounts(0, 0, 0, 0);
const TWO_ROWS = [row('2026-09-16', ALL_AMOUNTS), row('2026-09-30', OTHER_AMOUNTS)];

/** Occurrence as the bonus and reward rules read it (a type alias, like the amounts, so a case input is assignable to Json). An absent `periodOwnerId` means "not frozen" (the assignee); `null` means frozen as unassigned. */
type OccInput = {
  status: 'open' | 'done' | 'skipped';
  plannedDate: string;
  date: string;
  recordedDone?: boolean;
  assigneeId: string | null;
  periodOwnerId?: string | null;
  completedBy: string | null;
  completedAt: string | null;
};
type CtxInput = {
  anchor: string;
  timezone: string;
  today: string;
  schedule: BonusScheduleRow[];
  floor?: string;
};

const PERSON_A = 'a'.repeat(24);
const PERSON_B = 'b'.repeat(24);
const PERSON_C = 'c'.repeat(24);
const ALL_SCHEDULE = [row('2026-01-01', ALL_AMOUNTS)];

/** A planned occurrence of person A, done on time unless told otherwise (the `occ` of bonuses.test.ts). */
const occ = (plannedDate: string, overrides: Partial<OccInput> = {}): OccInput => ({
  status: 'done',
  plannedDate,
  date: plannedDate,
  recordedDone: false,
  assigneeId: PERSON_A,
  completedBy: PERSON_A,
  completedAt: `${plannedDate}T09:00:00.000Z`,
  ...overrides,
});
/** Cycle anchor Monday 14 Sep 2026: cycle 0 is 14 Sep to 11 Oct, cycle 1 is 12 Oct to 8 Nov. */
const ctx = (today: string, overrides: Partial<CtxInput> = {}): CtxInput => ({
  anchor: '2026-09-14',
  timezone: TZ,
  today,
  schedule: ALL_SCHEDULE,
  ...overrides,
});
const entriesCase = (
  name: string,
  items: OccInput[],
  today: string,
  overrides: Partial<CtxInput> = {},
): [string, { items: OccInput[]; context: CtxInput }] => [
  name,
  { items, context: ctx(today, overrides) },
];
const OPEN: Partial<OccInput> = { status: 'open', completedAt: null, completedBy: null };
const SKIPPED: Partial<OccInput> = { status: 'skipped', completedAt: null, completedBy: null };
const CUTOFF = '2026-09-20T22:00:00.000Z';
const oneAmount = (a: Partial<BonusAmounts>): BonusScheduleRow[] => [
  row('2026-01-01', { ...ZERO_AMOUNTS, ...a }),
];
const withoutRecordedDone = (o: OccInput): OccInput => {
  const copy = { ...o };
  delete copy.recordedDone;
  return copy;
};

const bonusesModule: ModuleSpec = {
  module: 'bonuses',
  functions: {
    bonusAmountsOn: fn(
      (i: { schedule: BonusScheduleRow[]; day: string }) => ({
        ...bonusAmountsOn(i.schedule, i.day),
      }),
      [
        ['an empty schedule gives all zero', { schedule: [], day: '2026-09-16' }],
        ['before the first row all amounts are zero', { schedule: TWO_ROWS, day: '2026-09-15' }],
        ['on the day of the first row it applies', { schedule: TWO_ROWS, day: '2026-09-16' }],
        ['between two rows the earlier one applies', { schedule: TWO_ROWS, day: '2026-09-29' }],
        ['on the day of the second row it applies', { schedule: TWO_ROWS, day: '2026-09-30' }],
        ['after the last row it keeps applying', { schedule: TWO_ROWS, day: '2027-01-01' }],
        [
          'an unsorted schedule still finds the last row on or before the day',
          { schedule: [TWO_ROWS[1]!, TWO_ROWS[0]!], day: '2026-10-05' },
        ],
        [
          'a row with all zero amounts is in force like any other',
          {
            schedule: [row('2026-09-16', ALL_AMOUNTS), row('2026-09-30', ZERO_AMOUNTS)],
            day: '2026-10-01',
          },
        ],
      ],
    ),
    sameBonusAmounts: fn(
      (i: { a: BonusAmounts; b: BonusAmounts }) => sameBonusAmounts(i.a, i.b),
      [
        ['equal amounts', { a: ALL_AMOUNTS, b: { ...ALL_AMOUNTS } }],
        ['one amount differs', { a: ALL_AMOUNTS, b: { ...ALL_AMOUNTS, cycleOnTime: 11 } }],
        ['all zero equals all zero', { a: ZERO_AMOUNTS, b: ZERO_AMOUNTS }],
      ],
    ),
    scheduleWithAmounts: fn(
      (i: { schedule: BonusScheduleRow[]; amounts: BonusAmounts; today: string }) =>
        scheduleWithAmounts(i.schedule, i.amounts, i.today) as unknown as Json,
      [
        [
          'amounts equal to the ones in force change nothing, also all zero on an empty schedule',
          { schedule: [], amounts: ZERO_AMOUNTS, today: '2026-09-16' },
        ],
        [
          'first amounts become a row from today',
          { schedule: [], amounts: ALL_AMOUNTS, today: '2026-09-16' },
        ],
        [
          'the same amounts again change nothing',
          { schedule: [row('2026-09-16', ALL_AMOUNTS)], amounts: ALL_AMOUNTS, today: '2026-09-16' },
        ],
        [
          'the same amounts on a later day change nothing',
          { schedule: [row('2026-09-16', ALL_AMOUNTS)], amounts: ALL_AMOUNTS, today: '2026-09-20' },
        ],
        [
          'other amounts replace the row that starts today',
          {
            schedule: [row('2026-09-16', ALL_AMOUNTS)],
            amounts: amounts(6, 3, 20, 10),
            today: '2026-09-16',
          },
        ],
        [
          'other amounts on a later day add a row and keep the earlier one',
          {
            schedule: [row('2026-09-16', ALL_AMOUNTS)],
            amounts: OTHER_AMOUNTS,
            today: '2026-09-30',
          },
        ],
        [
          'switching a kind off is a row with a zero',
          { schedule: TWO_ROWS, amounts: amounts(1, 2, 3, 0), today: '2026-09-30' },
        ],
        [
          'a row that starts in the future is kept and sorted behind the new row',
          {
            schedule: [row('2026-10-15', OTHER_AMOUNTS)],
            amounts: ALL_AMOUNTS,
            today: '2026-09-16',
          },
        ],
        [
          'amounts equal to the ones in force but a future row exists change nothing',
          {
            schedule: [row('2026-09-16', ALL_AMOUNTS), row('2026-10-15', OTHER_AMOUNTS)],
            amounts: ALL_AMOUNTS,
            today: '2026-09-20',
          },
        ],
      ],
    ),
    isBonusKind: fn(
      (i: { kind: string }) => isBonusKind(i.kind),
      [
        ['bonus_week_done', { kind: 'bonus_week_done' }],
        ['bonus_week_ontime', { kind: 'bonus_week_ontime' }],
        ['bonus_cycle_done', { kind: 'bonus_cycle_done' }],
        ['bonus_cycle_ontime', { kind: 'bonus_cycle_ontime' }],
        ['execution is not a bonus kind', { kind: 'execution' }],
        ['redemption is not a bonus kind', { kind: 'redemption' }],
        ['a prefix is not enough', { kind: 'bonus_week' }],
        ['the match is case sensitive', { kind: 'BONUS_WEEK_DONE' }],
        ['empty text', { kind: '' }],
      ],
    ),
    weekOf: fn(
      (i: { day: string }) => ({ ...weekOf(i.day) }),
      [
        ['a Wednesday', { day: '2026-09-16' }],
        ['the Sunday that ends the week', { day: '2026-09-20' }],
        ['the Monday that starts the week', { day: '2026-09-14' }],
        ['the week of the spring DST switch', { day: '2026-03-29' }],
        ['the week of the autumn DST switch', { day: '2026-10-25' }],
        ['a week across the year boundary', { day: '2026-12-31' }],
        ['a week with a leap day', { day: '2028-02-29' }],
        ['an invalid day key throws', { day: 'nope' }],
      ],
    ),
    cycleOf: fn(
      (i: { day: string; anchor: string }) => ({ ...cycleOf(i.day, i.anchor) }),
      [
        ['the last day of cycle 0', { day: '2026-10-11', anchor: '2026-09-14' }],
        ['the first day of cycle 1', { day: '2026-10-12', anchor: '2026-09-14' }],
        ['the anchor itself', { day: '2026-09-14', anchor: '2026-09-14' }],
        ['the day before the anchor is cycle -1', { day: '2026-09-13', anchor: '2026-09-14' }],
        ['far before the anchor', { day: '2025-01-01', anchor: '2026-09-14' }],
        ['a cycle across the spring DST switch', { day: '2026-03-29', anchor: '2026-09-14' }],
        ['a cycle across the year boundary', { day: '2026-12-31', anchor: '2026-09-14' }],
        ['a non-Monday anchor throws', { day: '2026-09-16', anchor: '2026-09-15' }],
        ['an invalid day key throws', { day: 'x', anchor: '2026-09-14' }],
      ],
    ),
    periodEnded: fn(
      (i: { period: { end: string }; today: string }) => periodEnded(i.period, i.today),
      [
        ['not ended on its last day', { period: { end: '2026-09-20' }, today: '2026-09-20' }],
        [
          'ended the day after its last day',
          { period: { end: '2026-09-20' }, today: '2026-09-21' },
        ],
        ['not ended before it started', { period: { end: '2026-09-20' }, today: '2026-09-14' }],
        ['ended long after', { period: { end: '2026-09-20' }, today: '2027-09-20' }],
        [
          'not ended the day before its last day',
          { period: { end: '2026-09-20' }, today: '2026-09-19' },
        ],
      ],
    ),
    onTimeCutoff: fn(
      (i: { period: { end: string }; timezone: string }) => iso(onTimeCutoff(i.period, i.timezone)),
      [
        [
          'autumn DST week ends at 23:00Z (169 hours)',
          { period: { end: '2026-10-25' }, timezone: TZ },
        ],
        [
          'spring DST week ends at 22:00Z (167 hours)',
          { period: { end: '2026-03-29' }, timezone: TZ },
        ],
        ['a summer week ends at 22:00Z', { period: { end: '2026-09-20' }, timezone: TZ }],
        ['a winter week ends at 23:00Z', { period: { end: '2026-01-04' }, timezone: TZ }],
        ['UTC has no offset', { period: { end: '2026-09-20' }, timezone: 'UTC' }],
        ['the last day of a leap year', { period: { end: '2028-12-31' }, timezone: TZ }],
        ['the leap day', { period: { end: '2028-02-29' }, timezone: TZ }],
        ['another timezone', { period: { end: '2026-09-20' }, timezone: 'America/New_York' }],
        ['an unknown timezone throws', { period: { end: '2026-09-20' }, timezone: 'Mars/Base' }],
        ['an invalid day key throws', { period: { end: 'x' }, timezone: 'UTC' }],
      ],
    ),
    periodDayOf: fn(
      (i: { occurrence: OccInput }) => periodDayOf(i.occurrence),
      [
        [
          'planned work uses the planned day, also after a reschedule',
          { occurrence: occ('2026-09-16', { date: '2026-09-23' }) },
        ],
        [
          'a missing recordedDone means planned work',
          { occurrence: withoutRecordedDone(occ('2026-09-16', { date: '2026-09-23' })) },
        ],
        [
          'recorded work uses its date',
          { occurrence: occ('2026-09-16', { date: '2026-09-23', recordedDone: true }) },
        ],
      ],
    ),
    periodOwnerOf: fn(
      (i: { occurrence: OccInput }) => periodOwnerOf(i.occurrence),
      [
        ['no frozen owner means the assignee', { occurrence: occ('2026-09-14') }],
        [
          'a frozen owner wins over the assignee',
          { occurrence: occ('2026-09-14', { periodOwnerId: PERSON_B }) },
        ],
        [
          'a frozen null owner is nobody, although there is an assignee',
          { occurrence: occ('2026-09-14', { periodOwnerId: null }) },
        ],
        [
          'unassigned without a frozen owner is nobody',
          { occurrence: occ('2026-09-14', { assigneeId: null }) },
        ],
      ],
    ),
    creditedOf: fn(
      (i: { occurrence: OccInput }) => creditedOf(i.occurrence),
      [
        [
          'done work is credited to completedBy',
          { occurrence: occ('2026-09-14', { completedBy: PERSON_B }) },
        ],
        [
          'done work without completedBy is credited to the assignee',
          { occurrence: occ('2026-09-14', { completedBy: null }) },
        ],
        [
          'done unassigned work without completedBy is credited to nobody',
          { occurrence: occ('2026-09-14', { completedBy: null, assigneeId: null }) },
        ],
        [
          'open work is credited to nobody, also with a completedBy',
          { occurrence: occ('2026-09-14', { status: 'open', completedBy: PERSON_B }) },
        ],
        [
          'skipped work is credited to nobody',
          { occurrence: occ('2026-09-14', { status: 'skipped', completedBy: PERSON_B }) },
        ],
      ],
    ),
    placementsOf: fn(
      (i: { occurrence: OccInput }) =>
        placementsOf(i.occurrence).map((p) => ({
          person: p.person,
          status: p.item.status,
          date: p.item.date,
          recordedDone: p.item.recordedDone === true,
          completedBy: p.item.completedBy,
          completedAt: p.item.completedAt,
        })),
      [
        ["done by the owner is the owner's done item", { occurrence: occ('2026-09-14') }],
        ["skipped work is the owner's open item", { occurrence: occ('2026-09-14', SKIPPED) }],
        [
          "unassigned open work is in nobody's set",
          { occurrence: occ('2026-09-14', { ...OPEN, assigneeId: null }) },
        ],
        [
          'done by somebody else: open for the owner, non-blocking for the doer',
          { occurrence: occ('2026-09-14', { completedBy: PERSON_B }) },
        ],
        [
          'done by somebody else, owner unassigned: only the doer has an item',
          {
            occurrence: occ('2026-09-14', {
              assigneeId: null,
              periodOwnerId: null,
              completedBy: PERSON_B,
            }),
          },
        ],
        [
          'the non-blocking item of the doer takes the planned day as its date',
          {
            occurrence: occ('2026-09-14', {
              date: '2026-09-23',
              completedBy: PERSON_B,
              completedAt: '2026-09-23T10:00:00.000Z',
            }),
          },
        ],
        [
          'recorded work belongs to the credited person',
          { occurrence: occ('2026-09-14', { recordedDone: true, assigneeId: PERSON_B }) },
        ],
        [
          "recorded work without a credited person is in nobody's set",
          {
            occurrence: occ('2026-09-14', {
              recordedDone: true,
              completedBy: null,
              assigneeId: null,
            }),
          },
        ],
        [
          'a frozen owner keeps the open item when somebody else did the work',
          {
            occurrence: occ('2026-09-14', {
              assigneeId: PERSON_C,
              periodOwnerId: PERSON_B,
              completedBy: PERSON_C,
            }),
          },
        ],
        [
          'done work whose frozen owner is the doer is a single done item',
          {
            occurrence: occ('2026-09-14', {
              assigneeId: PERSON_C,
              periodOwnerId: PERSON_B,
              completedBy: PERSON_B,
            }),
          },
        ],
        [
          'open work follows the frozen owner',
          { occurrence: occ('2026-09-14', { ...OPEN, periodOwnerId: PERSON_B }) },
        ],
      ],
    ),
    evaluateSet: fn(
      (i: { set: OccInput[]; cutoff: string }) => ({ ...evaluateSet(i.set, new Date(i.cutoff)) }),
      [
        [
          'counts the states and needs a planned occurrence to be eligible',
          {
            set: [occ('2026-09-14'), occ('2026-09-15', OPEN), occ('2026-09-16', SKIPPED)],
            cutoff: CUTOFF,
          },
        ],
        ['an empty set earns nothing', { set: [], cutoff: CUTOFF }],
        [
          'recorded work alone is done and on time but not eligible',
          { set: [occ('2026-09-14', { recordedDone: true })], cutoff: CUTOFF },
        ],
        [
          'one millisecond before the cut-off is on time',
          { set: [occ('2026-09-14', { completedAt: '2026-09-20T21:59:59.999Z' })], cutoff: CUTOFF },
        ],
        [
          'at the cut-off is late',
          { set: [occ('2026-09-14', { completedAt: '2026-09-20T22:00:00.000Z' })], cutoff: CUTOFF },
        ],
        [
          'done work without completedAt is late',
          { set: [occ('2026-09-14', { completedAt: null })], cutoff: CUTOFF },
        ],
        [
          'recorded work is on time whatever its completion instant says',
          {
            set: [
              occ('2026-09-16', {
                recordedDone: true,
                date: '2026-09-16',
                completedAt: '2026-09-23T10:00:00.000Z',
              }),
            ],
            cutoff: CUTOFF,
          },
        ],
        [
          'recorded work that is not marked done does not block',
          {
            set: [occ('2026-09-16', { recordedDone: true, status: 'open', completedAt: null })],
            cutoff: CUTOFF,
          },
        ],
        [
          'a skipped item blocks all-done',
          { set: [occ('2026-09-14'), occ('2026-09-15', SKIPPED)], cutoff: CUTOFF },
        ],
        [
          'one late item makes the set done but not on time',
          {
            set: [
              occ('2026-09-14'),
              occ('2026-09-15', { completedAt: '2026-09-25T10:00:00.000Z' }),
            ],
            cutoff: CUTOFF,
          },
        ],
        [
          'recorded work next to on-time planned work keeps the set on time',
          {
            set: [occ('2026-09-14'), occ('2026-09-15', { recordedDone: true })],
            cutoff: CUTOFF,
          },
        ],
      ],
    ),
    bonusKey: fn(
      (i: {
        kind: 'bonus_week_done' | 'bonus_week_ontime' | 'bonus_cycle_done' | 'bonus_cycle_ontime';
        personId: string;
        periodStart: string;
      }) => bonusKey(i.kind, i.personId, i.periodStart),
      [
        [
          'a week bonus',
          { kind: 'bonus_week_done', personId: PERSON_A, periodStart: '2026-09-14' },
        ],
        [
          'an on-time week bonus',
          { kind: 'bonus_week_ontime', personId: PERSON_B, periodStart: '2026-09-21' },
        ],
        [
          'a cycle is keyed by its first day',
          { kind: 'bonus_cycle_done', personId: PERSON_A, periodStart: '2026-08-17' },
        ],
        [
          'an on-time cycle bonus',
          { kind: 'bonus_cycle_ontime', personId: PERSON_C, periodStart: '2026-09-14' },
        ],
      ],
    ),
    expectedBonusEntries: fn(
      (i: { items: OccInput[]; context: CtxInput }) =>
        expectedBonusEntries(i.items, i.context) as unknown as Json,
      [
        entriesCase(
          'an on-time week gets both week bonuses, in addition to each other',
          [occ('2026-09-14'), occ('2026-09-16')],
          '2026-09-21',
        ),
        entriesCase(
          'a week that has not ended is not paid, also when complete',
          [occ('2026-09-14')],
          '2026-09-20',
        ),
        entriesCase('a week is not paid on its first day', [occ('2026-09-14')], '2026-09-14'),
        entriesCase('a week is paid the day after its last day', [occ('2026-09-14')], '2026-09-21'),
        entriesCase('an empty set pays nothing', [], '2026-09-21'),
        entriesCase(
          'recorded work alone earns nothing',
          [occ('2026-09-16', { recordedDone: true, plannedDate: '2026-09-16' })],
          '2026-09-21',
        ),
        entriesCase(
          'recorded work counts inside a set that has planned work',
          [occ('2026-09-16', { recordedDone: true, plannedDate: '2026-09-16' }), occ('2026-09-14')],
          '2026-09-21',
        ),
        entriesCase(
          'recorded work is placed by its date and is never late',
          [
            occ('2026-09-10', {
              recordedDone: true,
              date: '2026-09-16',
              completedAt: '2026-09-16T10:00:00.000Z',
            }),
            occ('2026-09-14'),
          ],
          '2026-09-21',
        ),
        entriesCase(
          'autumn DST week (169 hours): 22:59:59.999Z is on time',
          [occ('2026-10-19', { completedAt: '2026-10-25T22:59:59.999Z' })],
          '2026-10-26',
        ),
        entriesCase(
          'autumn DST week: 23:00Z is late',
          [occ('2026-10-19', { completedAt: '2026-10-25T23:00:00.000Z' })],
          '2026-10-26',
        ),
        entriesCase(
          'spring DST week (167 hours): 21:59:59.999Z is on time, and the cycle ends the same day',
          [occ('2026-03-23', { completedAt: '2026-03-29T21:59:59.999Z' })],
          '2026-03-30',
        ),
        entriesCase(
          'spring DST week: 22:00Z is late',
          [occ('2026-03-23', { completedAt: '2026-03-29T22:00:00.000Z' })],
          '2026-03-30',
        ),
        entriesCase(
          'a complete cycle pays the cycle bonuses and the four week bonuses once it has ended',
          ['2026-09-14', '2026-09-21', '2026-09-28', '2026-10-05'].map((day) => occ(day)),
          '2026-10-12',
        ),
        entriesCase(
          'the cycle has not ended on its last day',
          ['2026-09-14', '2026-09-21', '2026-09-28', '2026-10-05'].map((day) => occ(day)),
          '2026-10-11',
        ),
        entriesCase(
          'a partial first cycle earns the whole amount',
          [occ('2026-10-05')],
          '2026-10-12',
        ),
        entriesCase(
          'a cycle before the anchor is keyed by its first day',
          [occ('2026-08-20')],
          '2026-09-14',
        ),
        entriesCase(
          'an open occurrence blocks the cycle bonus but not the complete weeks',
          [occ('2026-09-14'), occ('2026-09-21'), occ('2026-09-28', OPEN), occ('2026-10-05')],
          '2026-10-12',
        ),
        entriesCase(
          'open or skipped work is not paid',
          [occ('2026-09-14', SKIPPED), occ('2026-09-15')],
          '2026-09-21',
        ),
        entriesCase(
          'work skipped and completed after the week is done but late',
          [occ('2026-09-14', { completedAt: '2026-09-25T10:00:00.000Z' }), occ('2026-09-15')],
          '2026-09-26',
        ),
        entriesCase(
          'a late check-off inside the cycle pays done for the week and the cycle, never on time',
          [occ('2026-09-14', { completedAt: '2026-10-12T10:00:00.000Z' })],
          '2026-10-13',
        ),
        entriesCase(
          'done work without completedAt is done but never on time',
          [occ('2026-09-14', { completedAt: null })],
          '2026-09-21',
        ),
        entriesCase(
          'rescheduled from week 1 to week 2 and completed there: late for the week, on time for the cycle',
          [occ('2026-09-16', { date: '2026-09-23', completedAt: '2026-09-23T10:00:00.000Z' })],
          '2026-10-12',
        ),
        entriesCase(
          'rescheduled and still open: it keeps blocking the week it was planned in',
          [occ('2026-09-16', { ...OPEN, date: '2026-09-23' }), occ('2026-09-14')],
          '2026-09-21',
        ),
        entriesCase(
          'rescheduled and still open: the week of the new date holds nothing of it',
          [
            occ('2026-09-16', { ...OPEN, date: '2026-09-23' }),
            occ('2026-09-21', { assigneeId: PERSON_B, completedBy: PERSON_B }),
          ],
          '2026-09-28',
        ),
        entriesCase(
          'a take-over is credited to the actor without blocking the assignee',
          [occ('2026-09-14', { assigneeId: PERSON_B, completedBy: PERSON_B }), occ('2026-09-15')],
          '2026-09-21',
        ),
        entriesCase(
          'a third person who did the work is not eligible without work of their own',
          [occ('2026-09-14', { assigneeId: PERSON_A, completedBy: PERSON_C })],
          '2026-09-21',
        ),
        entriesCase(
          'a third person with work of their own is paid, the assignee is not',
          [
            occ('2026-09-14', { assigneeId: PERSON_A, completedBy: PERSON_C }),
            occ('2026-09-15', { assigneeId: PERSON_C, completedBy: PERSON_C }),
          ],
          '2026-09-21',
        ),
        entriesCase(
          'unassigned open work blocks nobody',
          [occ('2026-09-14'), occ('2026-09-15', { ...OPEN, assigneeId: null })],
          '2026-09-21',
        ),
        entriesCase(
          'unassigned done work counts for the person who did it',
          [
            occ('2026-09-15', { assigneeId: null, completedBy: PERSON_B }),
            occ('2026-09-14', { assigneeId: PERSON_B, completedBy: PERSON_B }),
          ],
          '2026-09-21',
        ),
        entriesCase(
          'unassigned done work without anybody credited belongs to nobody',
          [occ('2026-09-15', { assigneeId: null, completedBy: null })],
          '2026-09-21',
        ),
        entriesCase(
          'every person is evaluated on their own set',
          [occ('2026-09-14'), occ('2026-09-15', { ...OPEN, assigneeId: PERSON_B })],
          '2026-09-21',
        ),
        entriesCase(
          'taking over overdue work of another keeps the finalised week bonus of the taker and gives the owner nothing',
          [
            occ('2026-09-14'),
            occ('2026-09-14', { assigneeId: PERSON_B, completedBy: PERSON_B }),
            occ('2026-09-15', {
              assigneeId: PERSON_A,
              periodOwnerId: PERSON_B,
              completedBy: PERSON_A,
              completedAt: '2026-09-23T10:00:00.000Z',
            }),
          ],
          '2026-09-28',
        ),
        entriesCase(
          'taken-over overdue work is no bonus on its own',
          [
            occ('2026-09-15', {
              assigneeId: PERSON_A,
              periodOwnerId: PERSON_B,
              completedBy: PERSON_A,
              completedAt: '2026-09-23T10:00:00.000Z',
            }),
          ],
          '2026-09-28',
        ),
        entriesCase(
          'claimed unassigned overdue work is non-blocking for the claimer and for nobody else',
          [
            occ('2026-09-14', { assigneeId: PERSON_B, completedBy: PERSON_B }),
            occ('2026-09-15', {
              assigneeId: PERSON_B,
              periodOwnerId: null,
              completedBy: PERSON_B,
              completedAt: '2026-09-23T10:00:00.000Z',
            }),
          ],
          '2026-09-28',
        ),
        entriesCase(
          'still unassigned and open after the week blocks nobody',
          [
            occ('2026-09-14', { assigneeId: PERSON_B, completedBy: PERSON_B }),
            occ('2026-09-15', { ...OPEN, assigneeId: null, periodOwnerId: null }),
          ],
          '2026-09-28',
        ),
        entriesCase(
          "reassigned open overdue work does not move into the new assignee's ended week",
          [
            occ('2026-09-14'),
            occ('2026-09-14', { assigneeId: PERSON_B, completedBy: PERSON_B }),
            occ('2026-09-15', { ...OPEN, assigneeId: PERSON_A, periodOwnerId: PERSON_B }),
          ],
          '2026-09-28',
        ),
        entriesCase(
          'the owner who finishes overdue work late is done but not on time',
          [
            occ('2026-09-14', { assigneeId: PERSON_B, completedBy: PERSON_B }),
            occ('2026-09-15', {
              assigneeId: PERSON_B,
              periodOwnerId: PERSON_B,
              completedBy: PERSON_B,
              completedAt: '2026-09-23T10:00:00.000Z',
            }),
          ],
          '2026-09-28',
        ),
        entriesCase(
          'a completion on behalf of the owner inside the week is credited normally',
          [
            occ('2026-09-14', { assigneeId: PERSON_B, completedBy: PERSON_B }),
            occ('2026-09-15', {
              assigneeId: PERSON_B,
              periodOwnerId: PERSON_B,
              completedBy: PERSON_B,
              completedAt: '2026-09-18T10:00:00.000Z',
            }),
          ],
          '2026-09-28',
        ),
        entriesCase(
          'recorded work never blocks, also with a corrected date before its completion',
          [
            occ('2026-09-16', {
              recordedDone: true,
              date: '2026-09-16',
              completedAt: '2026-09-23T10:00:00.000Z',
            }),
            occ('2026-09-14'),
          ],
          '2026-09-28',
        ),
        entriesCase(
          'the floor skips periods that start before it, and keeps the ones that start on it',
          [occ('2026-09-14'), occ('2026-09-21')],
          '2026-09-28',
          { floor: '2026-09-21' },
        ),
        entriesCase(
          'no floor evaluates every period',
          [occ('2026-09-14'), occ('2026-09-21')],
          '2026-09-28',
        ),
        entriesCase(
          'a cycle that starts before the floor is skipped as a whole',
          [occ('2026-09-14'), occ('2026-09-21')],
          '2026-10-12',
          { floor: '2026-09-21' },
        ),
        entriesCase(
          'a cycle that starts on the floor is kept',
          [occ('2026-09-14'), occ('2026-09-21')],
          '2026-10-12',
          { floor: '2026-09-14' },
        ),
        entriesCase('an amount of 0 writes no entry, per kind', [occ('2026-09-14')], '2026-09-21', {
          schedule: oneAmount({ weekOnTime: 3 }),
        }),
        entriesCase('no schedule means no bonuses', [occ('2026-09-14')], '2026-09-21', {
          schedule: [],
        }),
        entriesCase(
          'a schedule that starts after the period ended pays nothing',
          [occ('2026-09-14')],
          '2026-09-28',
          { schedule: [row('2026-09-21', ALL_AMOUNTS)] },
        ),
        entriesCase(
          'a schedule row that starts on the last day of the period is in force',
          [occ('2026-09-14')],
          '2026-09-28',
          { schedule: [row('2026-09-20', ALL_AMOUNTS)] },
        ),
        entriesCase(
          'a later schedule change never alters an ended period',
          [occ('2026-09-14'), occ('2026-09-21')],
          '2026-09-28',
          {
            schedule: [
              row('2026-01-01', { ...ZERO_AMOUNTS, weekDone: 5 }),
              row('2026-09-21', { ...ZERO_AMOUNTS, weekDone: 50 }),
            ],
          },
        ),
        entriesCase(
          'the order of the entries does not depend on the order of the input (forward)',
          [
            occ('2026-09-21', { assigneeId: PERSON_B, completedBy: PERSON_B }),
            occ('2026-09-14'),
            occ('2026-09-21'),
          ],
          '2026-09-28',
        ),
        entriesCase(
          'the order of the entries does not depend on the order of the input (reversed)',
          [
            occ('2026-09-21'),
            occ('2026-09-14'),
            occ('2026-09-21', { assigneeId: PERSON_B, completedBy: PERSON_B }),
          ],
          '2026-09-28',
        ),
        entriesCase(
          'a different timezone moves the cut-off',
          [occ('2026-09-14', { completedAt: '2026-09-20T23:30:00.000Z' })],
          '2026-09-21',
          { timezone: 'UTC' },
        ),
        entriesCase('a non-Monday anchor throws', [occ('2026-09-14')], '2026-09-21', {
          anchor: '2026-09-15',
        }),
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
        ['a half rounds up, like Math.round (0.5 gives 1)', { minutes: 0.5 }],
        ['a half rounds up, not to even (2.5 gives 3)', { minutes: 2.5 }],
        ['a half rounds up (1.5 gives 2)', { minutes: 1.5 }],
        ['just below a half rounds down', { minutes: 2.4 }],
        ['a half rounds up at 44.5', { minutes: 44.5 }],
        ['a negative half is floored at one (-0.5)', { minutes: -0.5 }],
        ['a negative half is floored at one (-2.5)', { minutes: -2.5 }],
        ['a half just below the maximum rounds up to it', { minutes: 999.5 }],
        ['a fraction above the maximum is capped', { minutes: 1000.4 }],
      ],
    ),
  },
};

// ---------------------------------------------------------------------------------------
// validation (packages/shared/src/validation/plan.ts; scenarios of plan.test.ts plus edge cases)
// ---------------------------------------------------------------------------------------

interface PlanInput {
  [key: string]: Json;
  slots: PlanSlot[] & Json;
  tasks: PlanTask[] & Json;
  users: PlanUser[] & Json;
  intervals: Interval[];
}

const ANNA = 'a00000000000000000000001';
const BRAM = 'b00000000000000000000002';
const GONE = 'c00000000000000000000003';

const planUser = (_id: string, name: string, overrides: Partial<PlanUser> = {}): PlanUser => ({
  _id,
  name,
  active: true,
  unavailableWeekdays: [],
  dailyBudgetMinutes: { weekday: 60, weekend: 120 },
  maxDailyMinutes: { weekday: 480, weekend: 480 },
  ...overrides,
});
const PLAN_USERS: PlanUser[] = [
  planUser(ANNA, 'Anna', { unavailableWeekdays: [2] }),
  planUser(BRAM, 'Bram'),
  planUser(GONE, 'Oud', { active: false }),
];

const planTask = (
  id: string,
  intervalKey: string,
  durationMinutes: number,
  active = true,
): PlanTask => ({
  _id: id,
  name: id,
  intervalKey,
  durationMinutes,
  active,
});
const WEEKLY = planTask('weekly', '1w', 40);
const THREE_TIMES_WEEKLY = planTask('three-times-weekly', '3w', 10);
const TWICE = planTask('twice', '2w', 10);
const QUARTER = planTask('quarter', 'quarter', 90);
const MONTHLY = planTask('monthly', '4wk', 40);
const OLD = planTask('old', '1w', 10, false);
const PLAN_TASKS = [WEEKLY, TWICE, QUARTER, MONTHLY, OLD];

const planSlot = (
  taskId: string,
  weekIndex: number,
  weekday: number,
  assigneeId: string | null = BRAM,
): PlanSlot => ({ taskId, weekIndex, weekday, assigneeId });

/** Slots that satisfy every interval exactly with no budget issues. */
const fullPlan = (): PlanSlot[] => [
  ...[0, 1, 2, 3].map((w) => planSlot('weekly', w, 1)),
  ...[0, 1, 2, 3].flatMap((w) => [planSlot('twice', w, 3), planSlot('twice', w, 6)]),
  planSlot('monthly', 0, 5, ANNA),
];

const plan = (slots: PlanSlot[], overrides: Partial<ValidatePlanInput> = {}): PlanInput =>
  ({
    slots,
    tasks: PLAN_TASKS,
    users: PLAN_USERS,
    intervals: DEFAULT_INTERVALS.map((i) => ({ ...i })),
    ...overrides,
  }) as PlanInput;

const withUser = (id: string, overrides: Partial<PlanUser>): PlanUser[] =>
  PLAN_USERS.map((u) => (u._id === id ? { ...u, ...overrides } : u));

const threeTimesWeekly = (count: number): PlanSlot[] =>
  Array.from({ length: count }, (_, index) =>
    planSlot('three-times-weekly', Math.floor(index / 3), (index % 3) + 1),
  );

const budgetUser = planUser('u', 'U', { maxDailyMinutes: { weekday: 45, weekend: 75 } });
const bramBudgets = (overrides: Partial<PlanUser>): PlanUser[] => withUser(BRAM, overrides);

const validationModule: ModuleSpec = {
  module: 'validation',
  functions: {
    isWeekendDay: fn(
      (i: { weekday: number }) => isWeekendDay(i.weekday),
      [-1, 0, 1, 5, 6, 7].map((weekday): [string, { weekday: number }] => [
        `${weekday}`,
        { weekday },
      ]),
    ),
    budgetFor: fn(
      (i: { user: PlanUser & Json; weekday: number }) => budgetFor(i.user, i.weekday),
      [
        ['Saturday uses the weekend maximum', { user: budgetUser as PlanUser & Json, weekday: 6 }],
        ['Sunday uses the weekend maximum', { user: budgetUser as PlanUser & Json, weekday: 0 }],
        ['Friday uses the weekday maximum', { user: budgetUser as PlanUser & Json, weekday: 5 }],
        ['Monday uses the weekday maximum', { user: budgetUser as PlanUser & Json, weekday: 1 }],
      ],
    ),
    validatePlan: fn(
      (i: PlanInput) => JSON.parse(JSON.stringify(validatePlan(i))) as Json,
      [
        // ---- errors
        ['empty plan: every active grid task is short, nothing else', plan([])],
        [
          'empty plan without tasks, users or intervals',
          plan([], { tasks: [], users: [], intervals: [] }),
        ],
        ['accepts a plan that satisfies every rule', plan(fullPlan())],
        [
          'assignee unavailable on that weekday',
          plan([...fullPlan(), planSlot('weekly', 0, 2, ANNA)]),
        ],
        ['unknown task', plan([...fullPlan(), planSlot('nope', 0, 1)])],
        ['inactive task', plan([...fullPlan(), planSlot('old', 0, 1)])],
        [
          'unknown user',
          plan([...fullPlan(), planSlot('weekly', 0, 4, 'd00000000000000000000004')]),
        ],
        ['inactive user', plan([...fullPlan(), planSlot('weekly', 0, 4, GONE)])],
        ['weekIndex above range', plan([...fullPlan(), planSlot('weekly', 4, 1)])],
        ['negative weekIndex', plan([...fullPlan(), planSlot('weekly', -1, 1)])],
        ['weekday above range', plan([...fullPlan(), planSlot('weekly', 0, 7)])],
        ['negative weekday', plan([...fullPlan(), planSlot('weekly', 0, -1)])],
        ['last valid position: week 3, Sunday', plan([planSlot('weekly', 3, 0)])],
        ['first valid position: week 0, Sunday', plan([planSlot('weekly', 0, 0)])],
        [
          'allows the same unavailable weekday for another user',
          plan([...fullPlan(), planSlot('quarter', 0, 2, BRAM)]),
        ],
        [
          'allows an unassigned slot on any weekday',
          plan([...fullPlan(), planSlot('quarter', 0, 2, null)]),
        ],
        [
          'rejects the same task twice on the same day, even for different assignees',
          plan([...fullPlan(), planSlot('weekly', 0, 1, ANNA)]),
        ],
        [
          'allows the same task on the same weekday in different weeks',
          plan([planSlot('quarter', 0, 1), planSlot('quarter', 1, 1)]),
        ],
        [
          'a third placement on one day is reported again (each repeat is an error)',
          plan([
            planSlot('quarter', 0, 1),
            planSlot('quarter', 0, 1, ANNA),
            planSlot('quarter', 0, 1, null),
          ]),
        ],
        [
          'the same task twice on one day, both unassigned',
          plan([planSlot('quarter', 0, 1, null), planSlot('quarter', 0, 1, null)]),
        ],
        [
          'one slot with every problem: errors come in rule order',
          plan([planSlot('nope', 9, 9, 'd00000000000000000000004')]),
        ],
        [
          'out-of-range position skips the unavailability and duplicate checks',
          plan([planSlot('weekly', 4, 2, ANNA), planSlot('weekly', 4, 2, ANNA)]),
        ],
        [
          'inactive task: an error, but the slot still counts in the summary',
          plan([planSlot('old', 0, 1)]),
        ],
        [
          'unknown task twice on one day: unknown_task then duplicate_task_day',
          plan([planSlot('nope', 0, 1), planSlot('nope', 0, 1)]),
        ],
        [
          'errors follow slot order across several bad slots',
          plan([
            planSlot('weekly', 0, 7),
            planSlot('nope', 0, 1),
            planSlot('weekly', 5, 1, GONE),
            planSlot('old', 0, 3),
          ]),
        ],
        [
          'inactive user: minutes are not counted for anyone',
          plan([planSlot('weekly', 0, 1, GONE)]),
        ],
        [
          'unknown assignee: minutes are not counted for anyone',
          plan([planSlot('weekly', 0, 1, 'd00000000000000000000004')]),
        ],
        ['unavailable assignee still counts the minutes', plan([planSlot('weekly', 0, 2, ANNA)])],
        [
          'unavailable weekday is checked on Sunday too',
          plan([planSlot('weekly', 0, 0, ANNA)], {
            users: withUser(ANNA, { unavailableWeekdays: [0, 6] }),
          }),
        ],
        // ---- interval warnings
        [
          '2w with 5 slots is a warning, not an error',
          plan([
            ...fullPlan().filter((s) => s.taskId !== 'twice'),
            ...[0, 1, 2, 3, 4].map((i) => planSlot('twice', i % 4, i < 4 ? 3 : 4)),
          ]),
        ],
        [
          'three-times-weekly needs 12 slots per cycle: 11 is short',
          plan(threeTimesWeekly(11), { tasks: [THREE_TIMES_WEEKLY] }),
        ],
        [
          'three-times-weekly with all 12 slots is fine',
          plan(threeTimesWeekly(12), { tasks: [THREE_TIMES_WEEKLY] }),
        ],
        [
          'too many slots for the interval is a warning too',
          plan([...fullPlan(), planSlot('monthly', 1, 5, ANNA)]),
        ],
        [
          'warns for active tasks that are not placed at all',
          plan(fullPlan().filter((s) => s.taskId !== 'monthly')),
        ],
        [
          'skips the interval check when perCycle is null',
          plan([...fullPlan(), planSlot('quarter', 2, 4, null)]),
        ],
        [
          'reports placed/required per task, leaving out inactive tasks without slots',
          plan(fullPlan()),
        ],
        [
          'a task with an unknown interval key is not grid-planned',
          plan([planSlot('odd', 0, 1)], { tasks: [planTask('odd', 'mystery', 5)] }),
        ],
        [
          'interval warnings follow the task order, not the slot order',
          plan([planSlot('monthly', 0, 5, ANNA)], { tasks: [MONTHLY, WEEKLY, TWICE] }),
        ],
        [
          'an inactive task with slots is summarised without an interval warning',
          plan([planSlot('old', 0, 1), planSlot('old', 1, 1)], { tasks: [OLD] }),
        ],
        // ---- budgets and totals
        [
          'uses the weekday budget Monday-Friday',
          plan([planSlot('weekly', 1, 2), planSlot('monthly', 1, 2)], { tasks: [WEEKLY, MONTHLY] }),
        ],
        [
          'uses the weekend budget on Saturday',
          plan([planSlot('weekly', 0, 6), planSlot('monthly', 0, 6)], { tasks: [WEEKLY, MONTHLY] }),
        ],
        [
          'uses the weekend budget on Sunday',
          plan([planSlot('weekly', 0, 0), planSlot('monthly', 0, 0), planSlot('quarter', 0, 0)], {
            tasks: [WEEKLY, MONTHLY, QUARTER],
          }),
        ],
        [
          'exactly at budget is fine',
          plan([planSlot('weekly', 0, 1), planSlot('quarter', 0, 1, ANNA)], {
            tasks: [planTask('weekly', '1w', 60), QUARTER],
            users: [
              { ...PLAN_USERS[1]! },
              { ...PLAN_USERS[0]!, dailyBudgetMinutes: { weekday: 90, weekend: 120 } },
            ],
          }),
        ],
        [
          'one minute over the budget warns',
          plan([planSlot('weekly', 0, 1)], { tasks: [planTask('weekly', '1w', 61)] }),
        ],
        [
          'applies one shared weekday budget across Monday to Friday',
          plan([planSlot('weekly', 0, 1), planSlot('monthly', 0, 2)], { tasks: [WEEKLY, MONTHLY] }),
        ],
        [
          'the weekend budget is shared across Saturday and Sunday',
          plan(
            [planSlot('weekly', 0, 6), planSlot('monthly', 0, 0), planSlot('quarter', 0, 6, ANNA)],
            { tasks: [WEEKLY, MONTHLY, QUARTER] },
          ),
        ],
        [
          'summarises minutes per day per user with budgets, Monday first, for active users only',
          plan([planSlot('weekly', 0, 1, ANNA), planSlot('quarter', 0, 1, BRAM)]),
        ],
        [
          'warns when one day exceeds the configured daily maximum',
          plan([planSlot('weekly', 0, 1), planSlot('monthly', 0, 1)], {
            tasks: [WEEKLY, MONTHLY],
            users: bramBudgets({
              dailyBudgetMinutes: { weekday: 200, weekend: 200 },
              maxDailyMinutes: { weekday: 60, weekend: 90 },
            }),
          }),
        ],
        [
          'the daily maximum on a weekend day uses the weekend value',
          plan([planSlot('weekly', 2, 0), planSlot('monthly', 2, 0)], {
            tasks: [WEEKLY, MONTHLY],
            users: bramBudgets({
              dailyBudgetMinutes: { weekday: 200, weekend: 200 },
              maxDailyMinutes: { weekday: 480, weekend: 70 },
            }),
          }),
        ],
        [
          'overload per day and per week at once: weekly warnings first, then days Monday first',
          plan(
            [
              planSlot('weekly', 0, 1),
              planSlot('monthly', 0, 1),
              planSlot('quarter', 0, 0),
              planSlot('weekly', 1, 6),
            ],
            {
              tasks: [WEEKLY, MONTHLY, QUARTER],
              users: bramBudgets({ maxDailyMinutes: { weekday: 60, weekend: 60 } }),
            },
          ),
        ],
        [
          'two users over budget: warnings follow the user order',
          plan(
            [
              planSlot('weekly', 0, 1),
              planSlot('monthly', 0, 3, ANNA),
              planSlot('quarter', 0, 4, ANNA),
              planSlot('quarter', 0, 5, BRAM),
            ],
            { tasks: [WEEKLY, MONTHLY, QUARTER] },
          ),
        ],
        [
          'totals unassigned slots separately, not against any budget',
          plan([
            planSlot('quarter', 2, 3, null),
            planSlot('weekly', 2, 3, null),
            planSlot('twice', 2, 4, ANNA),
          ]),
        ],
        ['totals minutes per week per user', plan(fullPlan())],
        [
          'inactive users are left out of the summary',
          plan([planSlot('weekly', 0, 1)], { users: [PLAN_USERS[2]!, PLAN_USERS[1]!] }),
        ],
        [
          'no active users: only unassigned minutes are summarised',
          plan([planSlot('weekly', 0, 1, null)], { users: [PLAN_USERS[2]!] }),
        ],
      ],
    ),
  },
};

// ---------------------------------------------------------------------------------------
// points (packages/shared/src/points.ts; defaultPointsForDuration and the constants are in limits)
// ---------------------------------------------------------------------------------------

const pointsModule: ModuleSpec = {
  module: 'points',
  functions: {
    pointsToCents: fn(
      (i: { points: number; centsPerPoint: number }) => pointsToCents(i.points, i.centsPerPoint),
      [
        ['whole points times whole cents', { points: 7, centsPerPoint: 25 }],
        ['zero points', { points: 0, centsPerPoint: 25 }],
        ['a negative balance gives negative cents', { points: -4, centsPerPoint: 10 }],
        [
          'the maximum task points at the maximum factor',
          { points: 1000, centsPerPoint: MAX_CENTS_PER_POINT },
        ],
        ['a factor of 0 shows no money', { points: 120, centsPerPoint: 0 }],
        ['one point at one cent', { points: 1, centsPerPoint: 1 }],
        ['a large negative balance', { points: -1000, centsPerPoint: MAX_CENTS_PER_POINT }],
        [
          'a balance beyond 32 bits of cents',
          { points: 1_000_000_000, centsPerPoint: MAX_CENTS_PER_POINT },
        ],
        ['zero points at a zero factor', { points: 0, centsPerPoint: 0 }],
      ],
    ),
    isTwoDecimalCurrency: fn(
      (i: { code: string }) => isTwoDecimalCurrency(i.code),
      [
        ...['EUR', 'USD', 'GBP', 'CHF'].map((code): [string, { code: string }] => [
          `${code} has two fraction digits`,
          { code },
        ]),
        ...['JPY', 'KWD', 'BHD'].map((code): [string, { code: string }] => [
          `${code} does not have two fraction digits`,
          { code },
        ]),
        ['text that is not a code', { code: 'not a code' }],
        ['empty text', { code: '' }],
      ],
    ),
  },
  // Display only: the web app formats money with Intl.NumberFormat, the API sends whole cents.
  omitted: ['formatCents'],
};

// ---------------------------------------------------------------------------------------
// rewards (packages/shared/src/rewards.ts; scenarios of rewards.test.ts plus edge cases)
// ---------------------------------------------------------------------------------------

type GoalInput = OccInput & { points: number };
type AutomaticGoalInput = { planned: number; points: number };

/** An open occurrence of `anna` planned in the week of 14 to 20 September 2026 (the `occurrence` of rewards.test.ts). */
const goalOcc = (overrides: Partial<GoalInput> = {}): GoalInput => ({
  status: 'open',
  plannedDate: '2026-09-15',
  date: '2026-09-15',
  assigneeId: 'anna',
  completedBy: null,
  completedAt: null,
  points: 3,
  ...overrides,
});
const WEEK = { start: '2026-09-14', end: '2026-09-20' };
const goalCase = (
  name: string,
  items: GoalInput[],
  personId: string,
  period = WEEK,
): [string, { items: GoalInput[]; personId: string; period: { start: string; end: string } }] => [
  name,
  { items, personId, period },
];

const rewardsModule: ModuleSpec = {
  module: 'rewards',
  functions: {
    automaticGoal: fn(
      (i: { items: GoalInput[]; personId: string; period: { start: string; end: string } }) => ({
        ...automaticGoal(i.items, i.personId, i.period),
      }),
      [
        goalCase(
          'sums the points of the work planned for the person in the period, and counts it',
          [
            goalOcc({ points: 3 }),
            goalOcc({ plannedDate: '2026-09-14', date: '2026-09-14', points: 2 }),
            goalOcc({
              plannedDate: '2026-09-20',
              date: '2026-09-20',
              points: 1,
              status: 'done',
              completedBy: 'anna',
              completedAt: '2026-09-20T10:00:00.000Z',
            }),
          ],
          'anna',
        ),
        goalCase(
          'puts work in a period by the day it was planned, and includes both ends',
          [
            goalOcc({ plannedDate: '2026-09-10', date: '2026-09-16', points: 5 }),
            goalOcc({ plannedDate: '2026-09-21', date: '2026-09-18', points: 7 }),
            goalOcc({ plannedDate: '2026-09-14', date: '2026-09-14', points: 1 }),
            goalOcc({ plannedDate: '2026-09-20', date: '2026-09-20', points: 2 }),
          ],
          'anna',
        ),
        goalCase(
          'leaves out skipped work, recorded work and work of other people or nobody',
          [
            goalOcc({ status: 'skipped', points: 4 }),
            goalOcc({ recordedDone: true, status: 'done', completedBy: 'anna', points: 4 }),
            goalOcc({ assigneeId: 'bram', points: 4 }),
            goalOcc({ assigneeId: null, points: 4 }),
            goalOcc({ points: 1 }),
          ],
          'anna',
        ),
        goalCase(
          'follows the period owner: the frozen owner keeps work somebody else did (anna)',
          [
            goalOcc({
              assigneeId: 'bram',
              periodOwnerId: 'anna',
              status: 'done',
              completedBy: 'bram',
              points: 3,
            }),
            goalOcc({ assigneeId: 'bram', points: 2 }),
            goalOcc({ assigneeId: 'anna', periodOwnerId: null, points: 8 }),
          ],
          'anna',
        ),
        goalCase(
          'follows the period owner: no frozen owner means the assignee (bram)',
          [
            goalOcc({
              assigneeId: 'bram',
              periodOwnerId: 'anna',
              status: 'done',
              completedBy: 'bram',
              points: 3,
            }),
            goalOcc({ assigneeId: 'bram', points: 2 }),
            goalOcc({ assigneeId: 'anna', periodOwnerId: null, points: 8 }),
          ],
          'bram',
        ),
        goalCase('is empty without work', [], 'anna'),
        goalCase('counts planned work that is worth 0 points', [goalOcc({ points: 0 })], 'anna'),
        goalCase(
          'done work counts like open work',
          [
            goalOcc({
              status: 'done',
              completedBy: 'anna',
              completedAt: '2026-09-15T08:00:00.000Z',
              points: 4,
            }),
            goalOcc({ points: 6 }),
          ],
          'anna',
        ),
        goalCase(
          'a cycle period counts four weeks of work',
          [
            goalOcc({ plannedDate: '2026-09-14', date: '2026-09-14', points: 2 }),
            goalOcc({ plannedDate: '2026-10-11', date: '2026-10-11', points: 3 }),
            goalOcc({ plannedDate: '2026-10-12', date: '2026-10-12', points: 9 }),
          ],
          'anna',
          { start: '2026-09-14', end: '2026-10-11' },
        ),
        goalCase(
          'recorded work is left out also when it is the only work',
          [goalOcc({ recordedDone: true, status: 'done', completedBy: 'anna', points: 4 })],
          'anna',
        ),
      ],
    ),
    resolveRewardGoal: fn(
      (i: { explicit?: number | null; automatic: AutomaticGoalInput }) => ({
        ...resolveRewardGoal(i.explicit, i.automatic),
      }),
      [
        [
          'an explicit goal wins over the planned work',
          { explicit: 12, automatic: { planned: 2, points: 6 } },
        ],
        [
          'an explicit goal also without planned work',
          { explicit: 12, automatic: { planned: 0, points: 0 } },
        ],
        ['an explicit 0 is no goal', { explicit: 0, automatic: { planned: 2, points: 6 } }],
        [
          'the automatic goal is the planned points',
          { explicit: null, automatic: { planned: 2, points: 6 } },
        ],
        ['a missing explicit goal is automatic', { automatic: { planned: 1, points: 0 } }],
        [
          'planned work worth 0 points gives a goal of at least 1',
          { explicit: null, automatic: { planned: 3, points: 0 } },
        ],
        ['nothing planned gives no goal', { explicit: null, automatic: { planned: 0, points: 0 } }],
        [
          'nothing planned gives no goal, also with points',
          { explicit: null, automatic: { planned: 0, points: 5 } },
        ],
        [
          'the largest explicit goal',
          { explicit: MAX_REWARD_GOAL_POINTS, automatic: { planned: 1, points: 1 } },
        ],
      ],
    ),
    rewardPercent: fn(
      (i: { earnedPoints: number; goalPoints: number | null }) =>
        rewardPercent(i.earnedPoints, i.goalPoints),
      [
        ['nothing earned', { earnedPoints: 0, goalPoints: 10 }],
        ['a tenth', { earnedPoints: 1, goalPoints: 10 }],
        ['three quarters', { earnedPoints: 3, goalPoints: 4 }],
        ['a third rounds down', { earnedPoints: 1, goalPoints: 3 }],
        ['two thirds rounds down (66, not 67)', { earnedPoints: 2, goalPoints: 3 }],
        ['ninety percent', { earnedPoints: 9, goalPoints: 10 }],
        ['99 of 100', { earnedPoints: 99, goalPoints: 100 }],
        ['the goal exactly met', { earnedPoints: 10, goalPoints: 10 }],
        ['more than the goal is capped at 100', { earnedPoints: 25, goalPoints: 10 }],
        ['a negative balance is 0', { earnedPoints: -5, goalPoints: 10 }],
        ['999 of 1000 only reaches 99', { earnedPoints: 999, goalPoints: 1000 }],
        ['no goal is 0', { earnedPoints: 5, goalPoints: null }],
        ['a goal of 0 is 0', { earnedPoints: 5, goalPoints: 0 }],
        ['a negative goal is 0', { earnedPoints: 5, goalPoints: -3 }],
        ['half a percent rounds down', { earnedPoints: 1, goalPoints: 200 }],
        ['a half is 50', { earnedPoints: 5, goalPoints: 10 }],
        [
          'the largest goal, one point short',
          { earnedPoints: 99_999, goalPoints: MAX_REWARD_GOAL_POINTS },
        ],
        ['a balance beyond 32 bits of hundredths', { earnedPoints: 2_000_000_000, goalPoints: 3 }],
        ['a goal of 1 is met by 1 point', { earnedPoints: 1, goalPoints: 1 }],
        ['1 of 7 is 14 (14.28 rounds down)', { earnedPoints: 1, goalPoints: 7 }],
        ['6 of 7 is 85 (85.71 rounds down, not 86)', { earnedPoints: 6, goalPoints: 7 }],
      ],
    ),
    eggsForPercent: fn(
      (i: { percent: number }) => eggsForPercent(i.percent),
      [
        ...[0, 9, 10, 19, 20, 55, 90, 99, 100].map((percent): [string, { percent: number }] => [
          `${percent}%`,
          { percent },
        ]),
        ['a negative percentage clamps to 0', { percent: -20 }],
        ['a percentage above 100 clamps to 10 eggs', { percent: 250 }],
        ['a fraction below 10 is no egg', { percent: 9.99 }],
        ['a fraction above 10 is one egg', { percent: 10.5 }],
        ['just below 100 is 9 eggs', { percent: 99.9 }],
        ['just above 100 is 10 eggs', { percent: 100.1 }],
        ['a small negative fraction is 0', { percent: -0.5 }],
      ],
    ),
    sameRewardGoals: fn(
      (i: {
        a: { weekPoints: number | null; cyclePoints: number | null };
        b: { weekPoints: number | null; cyclePoints: number | null };
      }) => sameRewardGoals(i.a, i.b),
      [
        [
          'two automatic goals are the same',
          {
            a: { weekPoints: null, cyclePoints: null },
            b: { weekPoints: null, cyclePoints: null },
          },
        ],
        [
          'an explicit 0 differs from automatic',
          { a: { weekPoints: null, cyclePoints: null }, b: { weekPoints: 0, cyclePoints: null } },
        ],
        [
          'a different cycle goal differs',
          { a: { weekPoints: 1, cyclePoints: 2 }, b: { weekPoints: 1, cyclePoints: 3 } },
        ],
        [
          'equal explicit goals are the same',
          { a: { weekPoints: 5, cyclePoints: 20 }, b: { weekPoints: 5, cyclePoints: 20 } },
        ],
        [
          'a different week goal differs',
          { a: { weekPoints: 5, cyclePoints: null }, b: { weekPoints: 6, cyclePoints: null } },
        ],
      ],
    ),
  },
};

// ---------------------------------------------------------------------------------------
// badges (packages/shared/src/badges.ts; scenarios of badges.test.ts plus edge cases; the limits are in limits)
// ---------------------------------------------------------------------------------------

type RuleInput =
  | { type: 'executions' | 'minutes'; taskIds: string[]; threshold: number }
  | { type: 'onTimeWeeks'; threshold: number };
type RunInput = { id: string; taskId: string | null; minutes: number; at: string };

const TASK_1 = '0123456789abcdef01234567';
const TASK_2 = '89abcdef0123456789abcdef';
const run = (id: string, taskId: string | null, at: string, minutes = 10): RunInput => ({
  id,
  taskId,
  minutes,
  at,
});
const RUNS = [
  run('e3', TASK_1, '2026-09-18T08:00:00.000Z', 30),
  run('e1', TASK_1, '2026-09-16T08:00:00.000Z', 10),
  run('e2', TASK_2, '2026-09-17T08:00:00.000Z', 20),
  run('e4', null, '2026-09-19T08:00:00.000Z', 40),
];
const evalCase = (
  name: string,
  rule: RuleInput,
  executions: RunInput[],
  onTimeWeekDates: string[] = [],
): [string, { rule: RuleInput; executions: RunInput[]; onTimeWeekDates: string[] }] => [
  name,
  { rule, executions, onTimeWeekDates },
];
const WEEKS = ['2026-09-27T00:00:00.000Z', '2026-09-20T00:00:00.000Z', '2026-10-04T00:00:00.000Z'];
const text = (s: string): number[] => Array.from(new TextEncoder().encode(s));
const PNG = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const WEBP = [...text('RIFF'), 1, 0, 0, 0, ...text('WEBP'), ...text('VP8 ')];
const exampleKey = (key: string) => EXAMPLE_BADGES.find((e) => e.key === key)!;

const badgesModule: ModuleSpec = {
  module: 'badges',
  functions: {
    ruleCovers: fn(
      (i: { rule: RuleInput; taskId: string | null }) => ruleCovers(i.rule, i.taskId),
      [
        [
          'no chosen tasks covers every task',
          { rule: { type: 'executions', taskIds: [], threshold: 4 }, taskId: TASK_1 },
        ],
        [
          'no chosen tasks covers a one-off task',
          { rule: { type: 'executions', taskIds: [], threshold: 4 }, taskId: null },
        ],
        [
          'a chosen task is covered',
          { rule: { type: 'minutes', taskIds: [TASK_1, TASK_2], threshold: 1 }, taskId: TASK_2 },
        ],
        [
          'another task is not covered',
          { rule: { type: 'minutes', taskIds: [TASK_1], threshold: 1 }, taskId: TASK_2 },
        ],
        [
          'a one-off task never counts for chosen tasks',
          { rule: { type: 'executions', taskIds: [TASK_1, TASK_2], threshold: 1 }, taskId: null },
        ],
        [
          'an on-time-weeks rule covers no execution',
          { rule: { type: 'onTimeWeeks', threshold: 4 }, taskId: TASK_1 },
        ],
        [
          'an on-time-weeks rule covers no one-off task',
          { rule: { type: 'onTimeWeeks', threshold: 4 }, taskId: null },
        ],
      ],
    ),
    evaluateBadgeRule: fn(
      (i: { rule: RuleInput; executions: RunInput[]; onTimeWeekDates: string[] }) => ({
        ...evaluateBadgeRule(i.rule, i.executions, i.onTimeWeekDates),
      }),
      [
        evalCase(
          'executions of the chosen task: awarded at the threshold-th one',
          { type: 'executions', taskIds: [TASK_1], threshold: 2 },
          RUNS,
        ),
        evalCase(
          'the same, whatever order the executions come in',
          { type: 'executions', taskIds: [TASK_1], threshold: 2 },
          [...RUNS].reverse(),
        ),
        evalCase(
          'a threshold above the count is not reached',
          { type: 'executions', taskIds: [TASK_1], threshold: 3 },
          RUNS,
        ),
        evalCase(
          'no chosen task counts every task, a one-off task included',
          { type: 'executions', taskIds: [], threshold: 4 },
          RUNS,
        ),
        evalCase(
          'a one-off task never counts for chosen tasks',
          { type: 'executions', taskIds: [TASK_1, TASK_2], threshold: 1 },
          [run('e4', null, '2026-09-19T08:00:00.000Z')],
        ),
        evalCase(
          'a threshold of 1 is awarded at the first execution in time order',
          { type: 'executions', taskIds: [], threshold: 1 },
          RUNS,
        ),
        evalCase(
          'a threshold of 0 is never awarded by the count',
          { type: 'executions', taskIds: [], threshold: 0 },
          RUNS,
        ),
        evalCase('no executions at all', { type: 'executions', taskIds: [], threshold: 1 }, []),
        evalCase(
          'minutes: awarded at the execution that reaches the threshold, the total beyond it still reported',
          { type: 'minutes', taskIds: [], threshold: 55 },
          RUNS,
        ),
        evalCase(
          'minutes: a threshold met exactly by the last execution',
          { type: 'minutes', taskIds: [], threshold: 100 },
          RUNS,
        ),
        evalCase(
          'minutes: a threshold above the total is not reached',
          { type: 'minutes', taskIds: [], threshold: 101 },
          RUNS,
        ),
        evalCase(
          'minutes: a threshold met exactly at the first execution',
          { type: 'minutes', taskIds: [], threshold: 10 },
          RUNS,
        ),
        evalCase(
          'minutes: a threshold of 0 is awarded at the first execution',
          { type: 'minutes', taskIds: [], threshold: 0 },
          RUNS,
        ),
        evalCase(
          'minutes: a threshold of 0 without executions is not awarded',
          { type: 'minutes', taskIds: [], threshold: 0 },
          [],
        ),
        evalCase(
          'minutes of chosen tasks only',
          { type: 'minutes', taskIds: [TASK_1], threshold: 40 },
          RUNS,
        ),
        evalCase(
          'executions at the same moment are ordered by id (input order a, b)',
          { type: 'minutes', taskIds: [], threshold: 40 },
          [
            run('a', TASK_1, '2026-09-16T08:00:00.000Z', 50),
            run('b', TASK_1, '2026-09-16T08:00:00.000Z', 5),
          ],
        ),
        evalCase(
          'executions at the same moment are ordered by id (input order b, a)',
          { type: 'minutes', taskIds: [], threshold: 40 },
          [
            run('b', TASK_1, '2026-09-16T08:00:00.000Z', 5),
            run('a', TASK_1, '2026-09-16T08:00:00.000Z', 50),
          ],
        ),
        evalCase(
          'executions are ordered by their instant to the millisecond, not by id',
          { type: 'executions', taskIds: [], threshold: 2 },
          [
            run('z', TASK_1, '2026-09-16T08:00:00.001Z'),
            run('y', TASK_1, '2026-09-16T08:00:00.000Z'),
            run('x', TASK_1, '2026-09-17T08:00:00.000Z'),
          ],
        ),
        evalCase(
          'on-time weeks: awarded at the date of the threshold-th week',
          { type: 'onTimeWeeks', threshold: 2 },
          RUNS,
          WEEKS,
        ),
        evalCase(
          'on-time weeks: a threshold above the count is not reached',
          { type: 'onTimeWeeks', threshold: 4 },
          RUNS,
          WEEKS,
        ),
        evalCase('on-time weeks: none yet', { type: 'onTimeWeeks', threshold: 2 }, RUNS, []),
        evalCase('on-time weeks: the first week', { type: 'onTimeWeeks', threshold: 1 }, [], WEEKS),
        evalCase(
          'on-time weeks: a threshold of 0 is never awarded',
          { type: 'onTimeWeeks', threshold: 0 },
          [],
          WEEKS,
        ),
        evalCase(
          'on-time weeks: the last of three',
          { type: 'onTimeWeeks', threshold: 3 },
          [],
          WEEKS,
        ),
      ],
    ),
    sniffBadgeImageType: fn(
      (i: { bytes: number[] }) => sniffBadgeImageType(Uint8Array.from(i.bytes)),
      [
        ['a PNG signature with a byte after it', { bytes: [...PNG, 0] }],
        ['exactly the 8 bytes of a PNG signature', { bytes: PNG }],
        ['a PNG signature that is one byte short', { bytes: PNG.slice(0, 7) }],
        [
          'a PNG signature with one wrong byte',
          { bytes: [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0b] },
        ],
        ['a JPEG', { bytes: [0xff, 0xd8, 0xff, 0xe0] }],
        ['exactly the 3 bytes of a JPEG signature', { bytes: [0xff, 0xd8, 0xff] }],
        ['two bytes of a JPEG signature', { bytes: [0xff, 0xd8] }],
        ['a WebP', { bytes: WEBP }],
        ['exactly the 12 bytes of a WebP header', { bytes: WEBP.slice(0, 12) }],
        ['a WebP header that is one byte short', { bytes: WEBP.slice(0, 11) }],
        [
          'a RIFF file that is a WAVE',
          { bytes: [...text('RIFF'), 1, 0, 0, 0, ...text('WAVEfmt ')] },
        ],
        ['an SVG is refused', { bytes: text('<svg xmlns="http://www.w3.org/2000/svg"/>') }],
        ['a GIF is refused', { bytes: text('GIF89a') }],
        ['two bytes of a PNG signature', { bytes: [0x89, 0x50] }],
        ['an empty file', { bytes: [] }],
      ],
    ),
    // Constants of badges.ts: the document of the examples, and the match of their task-name patterns.
    exampleBadges: fn(
      () => EXAMPLE_BADGES.map((e) => JSON.parse(JSON.stringify(e)) as Json),
      [['the three examples of the "add example badges" action', {}]],
    ),
    exampleBadgeMatches: fn(
      (i: { key: string; taskName: string }) =>
        new RegExp(exampleKey(i.key).taskNamePattern!, 'i').test(i.taskName),
      [
        [
          'toilet matches Toilet schoonmaken',
          { key: 'example:toilet', taskName: 'Toilet schoonmaken' },
        ],
        ['toilet matches WC poetsen', { key: 'example:toilet', taskName: 'WC poetsen' }],
        ['toilet matches in capitals', { key: 'example:toilet', taskName: 'TOILET' }],
        ['toilet matches the word wc in lower case', { key: 'example:toilet', taskName: 'wc' }],
        [
          'toilet matches a longer word that starts with toilet',
          { key: 'example:toilet', taskName: 'Toiletborstel vervangen' },
        ],
        ['toilet does not match wc inside a word', { key: 'example:toilet', taskName: 'Zwcsdf' }],
        [
          'toilet matches wc next to a non-ASCII letter (\\b is ASCII only)',
          { key: 'example:toilet', taskName: 'éwc' },
        ],
        [
          'toilet does not match an unrelated task',
          { key: 'example:toilet', taskName: 'Stofzuigen' },
        ],
        ['mop matches Vloer dweilen', { key: 'example:mop', taskName: 'Vloer dweilen' }],
        ['mop matches Mop de keuken', { key: 'example:mop', taskName: 'Mop de keuken' }],
        ['mop matches zwabber', { key: 'example:mop', taskName: 'Terras zwabberen' }],
        [
          'mop matches a word that starts with mop',
          { key: 'example:mop', taskName: 'Mopje wassen' },
        ],
        ['mop does not match an unrelated task', { key: 'example:mop', taskName: 'Stofzuigen' }],
        ['mop does not match mop inside a word', { key: 'example:mop', taskName: 'Slimop' }],
      ],
    ),
  },
};

export const MODULES: ModuleSpec[] = [
  timeModule,
  cycleModule,
  dueModule,
  limitsModule,
  bonusesModule,
  pointsModule,
  rewardsModule,
  badgesModule,
  validationModule,
];

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
