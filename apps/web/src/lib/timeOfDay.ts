const TIME_OF_DAY_RE = /^([01]\d|2[0-3]):[0-5]\d$/;

/** Whether a value is a wall-clock time as `HH:mm` (the server validates the moments again). */
export function isTimeOfDay(value: string): boolean {
  return TIME_OF_DAY_RE.test(value);
}
