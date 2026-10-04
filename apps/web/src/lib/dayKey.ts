/**
 * Day-key arithmetic. A day key is a calendar date 'YYYY-MM-DD' of the household timezone. Adding days to one is
 * presentation plumbing, not a domain rule (plan §4.3): which cycle or week a day belongs to comes from the server
 * (`GET /api/v2/calendar`). The arithmetic runs on UTC midnights, so it never meets a DST change.
 */

const DAY_KEY = /^(\d{4})-(\d{2})-(\d{2})$/;
const DAY_MS = 86_400_000;

function parse(dayKey: string): number {
  const match = DAY_KEY.exec(dayKey);
  if (!match) throw new RangeError(`Invalid day key: ${dayKey}`);
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const time = Date.UTC(year, month - 1, day);
  const check = new Date(time);
  if (check.getUTCFullYear() !== year || check.getUTCMonth() !== month - 1 || check.getUTCDate() !== day) {
    throw new RangeError(`Invalid day key: ${dayKey}`);
  }
  return time;
}

/** 'YYYY-MM-DD' of an instant in a timezone, without pulling a date library into the bundle. */
export function dayKeyInZone(now: Date, timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(now);
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? '';
  return `${get('year')}-${get('month')}-${get('day')}`;
}

/** The day key `days` calendar days after (or, negative, before) the given one. */
export function addDays(dayKey: string, days: number): string {
  return new Date(parse(dayKey) + days * DAY_MS).toISOString().slice(0, 10);
}

/** Whole calendar days from `from` to `to` (negative when `to` is earlier). */
export function daysBetween(from: string, to: string): number {
  return Math.round((parse(to) - parse(from)) / DAY_MS);
}
