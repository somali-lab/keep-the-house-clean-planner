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

/** The Monday of the week of a day key (weeks start on Monday). A pure day-key string helper, no calendar rule of the household. */
export function mondayOfDay(dayKey: string): string {
  const weekday = new Date(parse(dayKey)).getUTCDay();
  return addDays(dayKey, -((weekday + 6) % 7));
}

/** Whole calendar days from `from` to `to` (negative when `to` is earlier). */
export function daysBetween(from: string, to: string): number {
  return Math.round((parse(to) - parse(from)) / DAY_MS);
}

const TIME_OF_DAY = /^([01]\d|2[0-3]):([0-5]\d)$/;

/** The offset of a timezone from UTC at an instant, in milliseconds (positive east of Greenwich). */
function zoneOffset(instant: number, timeZone: string): number {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone,
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).formatToParts(new Date(instant));
  const get = (type: string) => Number(parts.find((p) => p.type === type)?.value);
  const wall = Date.UTC(get('year'), get('month') - 1, get('day'), get('hour'), get('minute'), get('second'));
  return wall - Math.floor(instant / 1000) * 1000;
}

/**
 * The instant a wall-clock time ('HH:mm') occurs on a day key of the timezone. A time the day skips (the spring-forward gap) lands
 * the same distance after the gap; an ambiguous time (the fall-back overlap) means its first occurrence.
 */
export function fromDayKeyTime(dayKey: string, time: string, timeZone: string): Date {
  const match = TIME_OF_DAY.exec(time);
  if (!match) throw new RangeError(`Invalid time of day: ${time}`);
  const wall = parse(dayKey) + (Number(match[1]) * 60 + Number(match[2])) * 60_000;
  // The offsets on either side of the day tell which readings of the wall clock exist; a day holds at most one change.
  const before = zoneOffset(wall - DAY_MS, timeZone);
  const after = zoneOffset(wall + DAY_MS, timeZone);
  const candidates = [wall - before, wall - after].filter((instant) => zoneOffset(instant, timeZone) === wall - instant);
  return new Date(candidates.length > 0 ? Math.min(...candidates) : wall - before);
}
