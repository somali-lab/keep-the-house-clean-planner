import { describe, expect, it } from 'vitest';
import { formatCents, isTwoDecimalCurrency } from './money.ts';

describe('isTwoDecimalCurrency', () => {
  it('accepts currencies with two fraction digits and refuses the others', () => {
    for (const code of ['EUR', 'USD', 'GBP', 'CHF']) expect(isTwoDecimalCurrency(code)).toBe(true);
    for (const code of ['JPY', 'KWD', 'BHD', 'not a code', '']) expect(isTwoDecimalCurrency(code)).toBe(false);
  });
});

describe('formatCents', () => {
  it('formats whole cents in the currency and locale', () => {
    expect(formatCents(175, 'EUR', 'en-GB')).toBe('€1.75');
    expect(formatCents(175, 'EUR', 'nl-NL').replaceAll(' ', ' ')).toBe('€ 1,75');
    expect(formatCents(-5, 'USD', 'en-US')).toBe('-$0.05');
  });
});
