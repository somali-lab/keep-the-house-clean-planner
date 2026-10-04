import { describe, expect, it } from 'vitest';
import { isTimeOfDay } from './timeOfDay.ts';

describe('isTimeOfDay', () => {
  it('validates the HH:mm format', () => {
    expect(['00:00', '07:30', '23:59'].every(isTimeOfDay)).toBe(true);
    expect(['24:00', '7:30', '07:60', '0730', ''].some(isTimeOfDay)).toBe(false);
  });
});
