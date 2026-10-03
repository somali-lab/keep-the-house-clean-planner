/**
 * Badges (ADR-0014): pure rules over executions. A badge is awarded to a person when the audited
 * execution data crosses the threshold of its rule; the moment of the award is the moment of the
 * execution that crossed it, never the wall clock. Instants are ISO strings, ids are hex strings.
 */

export const MIN_BADGE_NAME_LENGTH = 1;
export const MAX_BADGE_NAME_LENGTH = 60;
export const MAX_BADGE_DESCRIPTION_LENGTH = 200;

/** An uploaded badge image is at most 256 KB. */
export const MAX_BADGE_IMAGE_BYTES = 256 * 1024;
/** Longest base64 text that can hold {@link MAX_BADGE_IMAGE_BYTES} bytes (4 characters per 3 bytes, with padding). */
export const MAX_BADGE_IMAGE_BASE64_LENGTH = Math.ceil(MAX_BADGE_IMAGE_BYTES / 3) * 4;
/** No SVG: an image that can carry script would run in the page that shows it (ADR-0014). */
export const BADGE_IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/webp'] as const;
export type BadgeImageType = (typeof BADGE_IMAGE_TYPES)[number];

/** Largest threshold of an executions or minutes rule, and of an on-time-weeks rule. */
export const MAX_BADGE_THRESHOLD = 100_000;
export const MAX_ON_TIME_WEEKS_THRESHOLD = 1000;
/** Most tasks one rule can name. */
export const MAX_BADGE_RULE_TASKS = 500;

/** Rule kinds. `executions` and `minutes` look at chosen tasks (none chosen = every task), `onTimeWeeks` at the on-time week bonuses. */
export const BADGE_RULE_TYPES = ['executions', 'minutes', 'onTimeWeeks'] as const;
export type BadgeRuleType = (typeof BADGE_RULE_TYPES)[number];

export type BadgeRule =
  | { type: 'executions' | 'minutes'; taskIds: string[]; threshold: number }
  | { type: 'onTimeWeeks'; threshold: number };

/** The part of a done occurrence that a rule needs, credited to one person. */
export interface BadgeExecution {
  id: string;
  /** Null for a one-off task. */
  taskId: string | null;
  /** The duration the occurrence had (`durationMinutesSnapshot`). */
  minutes: number;
  /** When the execution counts: its completion instant, else its date. */
  at: string;
}

export interface BadgeOutcome {
  /** How far the person is: executions, minutes or on-time weeks. It can exceed the threshold. */
  current: number;
  /** The moment the threshold was first crossed according to the data, or null while it is not reached. */
  awardedAt: string | null;
}

function byMoment(a: { at: string; id: string }, b: { at: string; id: string }): number {
  const delta = new Date(a.at).getTime() - new Date(b.at).getTime();
  if (delta !== 0) return delta;
  return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
}

/** Whether an execution counts for the rule: a one-off task only counts when no task was chosen. */
export function ruleCovers(rule: BadgeRule, taskId: string | null): boolean {
  if (rule.type === 'onTimeWeeks') return false;
  if (rule.taskIds.length === 0) return true;
  return taskId !== null && rule.taskIds.includes(taskId);
}

/**
 * Evaluates one rule for one person. `executions` are all done executions credited to the person;
 * `onTimeWeekDates` are the ISO instants (the last day of the week) of their on-time week bonuses.
 * The result is a pure function of its input, so recomputing never changes it.
 */
export function evaluateBadgeRule(rule: BadgeRule, executions: BadgeExecution[], onTimeWeekDates: string[]): BadgeOutcome {
  if (rule.type === 'onTimeWeeks') {
    const dates = [...onTimeWeekDates].sort((a, b) => new Date(a).getTime() - new Date(b).getTime());
    return { current: dates.length, awardedAt: dates.length >= rule.threshold ? (dates[rule.threshold - 1] ?? null) : null };
  }
  const counted = executions.filter((execution) => ruleCovers(rule, execution.taskId)).sort(byMoment);
  if (rule.type === 'executions') {
    return { current: counted.length, awardedAt: counted.length >= rule.threshold ? (counted[rule.threshold - 1]?.at ?? null) : null };
  }
  let total = 0;
  let awardedAt: string | null = null;
  for (const execution of counted) {
    total += execution.minutes;
    if (awardedAt === null && total >= rule.threshold) awardedAt = execution.at;
  }
  return { current: total, awardedAt };
}

/** The image type a file really is, from its first bytes; null for anything else (an SVG included). */
export function sniffBadgeImageType(bytes: Uint8Array): BadgeImageType | null {
  const at = (offset: number, ...values: number[]) => values.every((value, i) => bytes[offset + i] === value);
  if (bytes.length >= 8 && at(0, 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a)) return 'image/png';
  if (bytes.length >= 3 && at(0, 0xff, 0xd8, 0xff)) return 'image/jpeg';
  // RIFF <size> WEBP
  if (bytes.length >= 12 && at(0, 0x52, 0x49, 0x46, 0x46) && at(8, 0x57, 0x45, 0x42, 0x50)) return 'image/webp';
  return null;
}

export type BadgeLanguage = 'nl' | 'en';

export interface ExampleBadge {
  /** Stable key: the example is created once, however often the action runs. */
  key: string;
  /** Matches the names of the tasks the example is about; no match leaves the badge inactive until tasks are chosen. */
  taskNamePattern: string | null;
  rule: { type: BadgeRuleType; threshold: number };
  text: Record<BadgeLanguage, { name: string; description: string }>;
}

/** The examples of the "add example badges" action (ADR-0014); names, thresholds and tasks stay editable. */
export const EXAMPLE_BADGES: readonly ExampleBadge[] = [
  {
    key: 'example:on_time',
    taskNamePattern: null,
    rule: { type: 'onTimeWeeks', threshold: 4 },
    text: {
      nl: { name: 'Alles op tijd', description: 'Vier weken alles op tijd gedaan.' },
      en: { name: 'Always on time', description: 'Did everything on time for four weeks.' },
    },
  },
  {
    key: 'example:toilet',
    taskNamePattern: String.raw`toilet|\bwc\b`,
    rule: { type: 'executions', threshold: 10 },
    text: {
      nl: { name: 'Toiletjuffrouw', description: 'Het toilet 10 keer schoongemaakt.' },
      en: { name: 'Toilet Champion', description: 'Cleaned the toilet 10 times.' },
    },
  },
  {
    key: 'example:mop',
    taskNamePattern: String.raw`dweil|zwabber|\bmop`,
    rule: { type: 'minutes', threshold: 300 },
    text: {
      nl: { name: 'Dweilkampioen', description: '300 minuten gedweild.' },
      en: { name: 'Mop Champion', description: 'Mopped for 300 minutes.' },
    },
  },
];
