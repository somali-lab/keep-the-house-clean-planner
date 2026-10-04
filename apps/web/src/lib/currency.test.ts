import { describe, expect, it } from 'vitest';
import { isTwoDecimalCurrency } from './currency.ts';

describe('isTwoDecimalCurrency', () => {
  it('accepts currencies with two fraction digits and refuses the others', () => {
    for (const code of ['EUR', 'USD', 'GBP', 'CHF']) expect(isTwoDecimalCurrency(code)).toBe(true);
    for (const code of ['JPY', 'KWD', 'BHD', 'not a code', '']) expect(isTwoDecimalCurrency(code)).toBe(false);
  });
});
