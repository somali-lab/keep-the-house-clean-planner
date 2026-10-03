/** Smallest and largest points value of a task (ADR-0011). 0 means the task earns nothing. */
export const MIN_TASK_POINTS = 0;
export const MAX_TASK_POINTS = 1000;

/**
 * Default points of a task or one-off task: one point per minute, between 1 and the maximum
 * (ADR-0011).
 */
export function defaultPointsForDuration(minutes: number): number {
  if (!Number.isFinite(minutes)) return 1;
  return Math.min(MAX_TASK_POINTS, Math.max(1, Math.round(minutes)));
}

/** Smallest and largest conversion factor from points to currency, in cents per point (ADR-0013). 0 means no money is shown. */
export const MIN_CENTS_PER_POINT = 0;
export const MAX_CENTS_PER_POINT = 10000;

/** Currency of the household until an administrator picks another (ISO 4217). */
export const DEFAULT_CURRENCY_CODE = 'EUR';

/** Longest note of a redemption, in characters. */
export const MAX_REDEMPTION_NOTE_LENGTH = 200;

/**
 * Whether a currency has exactly two fraction digits, so that whole cents are its smallest unit (EUR and USD
 * yes; JPY with none and KWD with three no). Money is stored as integer cents, which only fits these currencies.
 * False for a code the runtime cannot format.
 */
export function isTwoDecimalCurrency(code: string): boolean {
  try {
    return new Intl.NumberFormat('en', { style: 'currency', currency: code }).resolvedOptions().maximumFractionDigits === 2;
  } catch {
    return false;
  }
}

/** Money is always whole cents: points times the factor, with no rounding needed. */
export function pointsToCents(points: number, centsPerPoint: number): number {
  return points * centsPerPoint;
}

/**
 * Formats whole cents in a currency for a locale with `Intl.NumberFormat`. Money is stored as integer
 * cents; the division only happens here, for display.
 */
export function formatCents(cents: number, currencyCode: string, locale: string): string {
  return new Intl.NumberFormat(locale, { style: 'currency', currency: currencyCode }).format(cents / 100);
}
