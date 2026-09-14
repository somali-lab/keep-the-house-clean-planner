import { getLanguage, getLocale } from '../../i18n/runtime.ts';

/** Rounds an axis maximum up to a clean number (1, 2, 2.5 or 5 × 10^n). */
export function niceMax(max: number): number {
  if (!(max > 0)) return 60;
  const exponent = 10 ** Math.floor(Math.log10(max));
  const fraction = max / exponent;
  const nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10;
  return nice * exponent;
}

/** Evenly spaced clean ticks from 0 to niceMax(max). */
export function ticks(max: number, count = 4): number[] {
  const top = niceMax(max);
  return Array.from({ length: count + 1 }, (_, i) => Math.round(((top / count) * i) * 100) / 100);
}

const numberFormat = () => new Intl.NumberFormat(getLocale());
const percentFormat = () =>
  new Intl.NumberFormat(getLocale(), { style: 'percent', maximumFractionDigits: 0 });
const factorFormat = () =>
  new Intl.NumberFormat(getLocale(), { minimumFractionDigits: 1, maximumFractionDigits: 1 });

export const formatNumber = (n: number) => numberFormat().format(n);
export const formatMinutes = (minutes: number) => `${numberFormat().format(minutes)} min`;
export const formatPercent = (rate: number | null) =>
  rate === null ? '—' : percentFormat().format(rate);
export const formatFactor = (factor: number) => `×${factorFormat().format(factor)}`;
export const formatDays = (days: number) =>
  `${factorFormat().format(days)} ${getLanguage() === 'nl' ? 'dagen' : 'days'}`;

/**
 * Categorical series colors, as CSS custom properties defined in styles.css.
 * Fixed order by entity (never by rank); slots 1–3 are validated all-pairs,
 * up to 8 adjacent. The caller folds anything past 8 into "Overig".
 */
export const MAX_SERIES = 8;
export const seriesColor = (slot: number) => `var(--series-${Math.min(slot, MAX_SERIES - 1) + 1})`;
