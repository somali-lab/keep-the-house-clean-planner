import { describe, expect, it } from 'vitest';
import { formatMoney } from './money.ts';

/** Intl puts a no-break space between the symbol and the amount in Dutch; the test compares with a plain space. */
const plain = (text: string) => text.replace(/\s/g, ' ');

describe('formatMoney', () => {
  it('shows whole cents in the currency and the language of the interface', () => {
    expect(plain(formatMoney(75, 'EUR', 'nl'))).toBe('€ 0,75');
    expect(formatMoney(75, 'EUR', 'en')).toBe('€0.75');
    expect(plain(formatMoney(123456, 'EUR', 'nl'))).toBe('€ 1.234,56');
    expect(plain(formatMoney(0, 'EUR', 'nl'))).toBe('€ 0,00');
  });

  it('formats a negative amount and another currency with the sign and symbol of that currency', () => {
    expect(formatMoney(-250, 'EUR', 'en')).toBe('-€2.50');
    expect(formatMoney(1000, 'USD', 'en')).toBe('$10.00');
  });
});
