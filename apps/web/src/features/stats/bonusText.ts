import { format } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import type { BonusLabel } from './pointsModel.ts';

/** "7 sep" in the interface language: the short form that labels the days of a cycle bonus. */
export function shortDate(dayKey: string): string {
  return new Intl.DateTimeFormat(getLocale(), { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(`${dayKey}T00:00:00Z`));
}

/** "Weekbonus: alles op tijd, week 40" or "Cyclusbonus: alles gedaan, 7 sep – 4 okt". */
export function bonusText(label: BonusLabel): string {
  return format(label.key, { week: label.week, from: shortDate(label.from), to: shortDate(label.to) });
}
