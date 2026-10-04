/**
 * Whether a currency has exactly two fraction digits, so that whole cents are its smallest unit (EUR and USD
 * yes; JPY with none and KWD with three no). False for a code the runtime cannot format. Only used to offer the currencies
 * that fit; the server validates the currency again.
 */
export function isTwoDecimalCurrency(code: string): boolean {
  try {
    return new Intl.NumberFormat('en', { style: 'currency', currency: code }).resolvedOptions().maximumFractionDigits === 2;
  } catch {
    return false;
  }
}
