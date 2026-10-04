/**
 * Display helpers for money, which the server keeps as whole cents (requirements 4.12). The web app only formats it and
 * offers the currencies that fit; the server validates every value it is sent.
 */

/**
 * Whether a currency has exactly two fraction digits, so that whole cents are its smallest unit (EUR and USD
 * yes; JPY with none and KWD with three no). False for a code the runtime cannot format.
 */
export function isTwoDecimalCurrency(code: string): boolean {
  try {
    return new Intl.NumberFormat('en', { style: 'currency', currency: code }).resolvedOptions().maximumFractionDigits === 2;
  } catch {
    return false;
  }
}

/** Formats whole cents in a currency for a locale with `Intl.NumberFormat`; the division only happens here, for display. */
export function formatCents(cents: number, currencyCode: string, locale: string): string {
  return new Intl.NumberFormat(locale, { style: 'currency', currency: currencyCode }).format(cents / 100);
}
