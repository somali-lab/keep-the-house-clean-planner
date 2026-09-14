import { describe, expect, it } from 'vitest';
import { exportUrl, isGenerated, isoWeekLabel, mondayOfDay, rangeGenerated, weekOptions } from './exportModel.ts';

const cycles = [
  { startDate: '2026-09-14', endDate: '2026-10-11' },
  { startDate: '2026-10-12', endDate: '2026-11-08' },
];

describe('ISO weeks', () => {
  it.each([
    ['2026-09-14', '2026-W38'],
    ['2026-09-20', '2026-W38'],
    ['2026-12-31', '2026-W53'],
    ['2027-01-03', '2026-W53'],
    ['2027-01-04', '2027-W01'],
    ['2025-12-29', '2026-W01'],
  ])('%s → %s', (day, label) => {
    expect(isoWeekLabel(day)).toBe(label);
  });

  it('finds the Monday of a week', () => {
    expect(mondayOfDay('2026-09-16')).toBe('2026-09-14');
    expect(mondayOfDay('2026-09-20')).toBe('2026-09-14');
    expect(mondayOfDay('2026-09-21')).toBe('2026-09-21');
  });
});

describe('availability', () => {
  it('lists the current week and the following weeks', () => {
    const options = weekOptions('2026-09-16');
    expect(options).toHaveLength(12);
    expect(options[0]).toEqual({ label: '2026-W38', monday: '2026-09-14', sunday: '2026-09-20' });
    expect(options.at(-1)?.label).toBe('2026-W49');
  });

  it('requires every week of a range to be generated', () => {
    expect(isGenerated('2026-11-08', cycles)).toBe(true);
    expect(isGenerated('2026-11-09', cycles)).toBe(false);
    expect(rangeGenerated('2026-10-12', 4, cycles)).toBe(true); // 12, 19, 26 Oct, 2 Nov
    expect(rangeGenerated('2026-10-19', 4, cycles)).toBe(false); // would need 9 Nov
  });
});

describe('exportUrl', () => {
  const base = { startWeek: '2026-W39', date: '2026-09-16', orientation: 'landscape' as const, totals: true };

  it('builds week URLs; orientation only applies to two weeks', () => {
    expect(exportUrl({ ...base, range: '2' })).toBe('/api/export/pdf?fromWeek=2026-W39&weeks=2&orientation=landscape&totals=true');
    expect(exportUrl({ ...base, range: '4' })).toBe('/api/export/pdf?fromWeek=2026-W39&weeks=4&orientation=portrait&totals=true');
  });

  it('builds day and due URLs', () => {
    expect(exportUrl({ ...base, range: 'day' })).toBe('/api/export/pdf/day?date=2026-09-16');
    expect(exportUrl({ ...base, range: 'due' })).toBe('/api/export/pdf/due');
  });

  it('passes English through to every PDF endpoint', () => {
    expect(exportUrl({ ...base, range: '2', language: 'en' })).toContain('language=en');
    expect(exportUrl({ ...base, range: 'day', language: 'en' })).toBe(
      '/api/export/pdf/day?date=2026-09-16&language=en',
    );
    expect(exportUrl({ ...base, range: 'due', language: 'en' })).toBe(
      '/api/export/pdf/due?language=en',
    );
  });
});
