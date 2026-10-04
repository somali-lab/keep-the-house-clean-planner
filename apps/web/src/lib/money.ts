/**
 * Formats money for display. The server delivers money as whole cents with the currency code (plan 4.3); the division by 100 and the
 * currency symbol are presentation, done here with `Intl.NumberFormat`. Only two-decimal currencies exist in the app, as the server
 * refuses any other currency for the conversion.
 */
export function formatMoney(cents: number, currencyCode: string, locale: string): string {
  return new Intl.NumberFormat(locale, { style: 'currency', currency: currencyCode }).format(cents / 100);
}
