import { describe, expect, it } from 'vitest';
import { calendarRoute } from '../../test/fixtures.ts';
import type { CalendarDay } from '../../api/v2/queries.ts';
import { exportUrl, isGenerated, rangeGenerated, tasksPdfUrl, weekOptions, weekOptionsRange } from './exportModel.ts';

const cycles = [
  { startDate: '2026-09-14', endDate: '2026-10-11' },
  { startDate: '2026-10-12', endDate: '2026-11-08' },
];

describe('availability', () => {
  it('lists the current week and the following weeks from the calendar of the server', async () => {
    const todayKey = '2026-09-16';
    const range = weekOptionsRange(todayKey);
    expect(range).toEqual({ from: '2026-09-10', to: '2026-12-09' });
    const answer = calendarRoute()(undefined, `/api/v2/calendar?from=${range.from}&to=${range.to}`) as { days: CalendarDay[] };
    const calendar = new Map(answer.days.map((day) => [day.dayKey, day]));
    const options = weekOptions(todayKey, calendar);
    expect(options).toHaveLength(12);
    expect(options[0]).toEqual({ label: '2026-W38', monday: '2026-09-14', sunday: '2026-09-20' });
    expect(options.at(-1)?.label).toBe('2026-W49');
  });

  it('has no options before the calendar has answered, and skips a week it does not know', () => {
    expect(weekOptions('2026-09-16', new Map())).toEqual([]);
    const day = (dayKey: string, weekStart: string, isoWeek: string) => [dayKey, { dayKey, weekStart, isoWeek } as CalendarDay] as const;
    const partial = new Map([day('2026-09-16', '2026-09-14', '2026-W38'), day('2026-09-14', '2026-09-14', '2026-W38')]);
    expect(weekOptions('2026-09-16', partial).map((o) => o.label)).toEqual(['2026-W38']);
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
    expect(exportUrl({ ...base, range: '2' })).toBe('/api/v2/export/pdf/schedule?fromWeek=2026-W39&weeks=2&orientation=landscape&totals=true');
    expect(exportUrl({ ...base, range: '4' })).toBe('/api/v2/export/pdf/schedule?fromWeek=2026-W39&weeks=4&orientation=portrait&totals=true');
  });

  it('builds day and due URLs', () => {
    expect(exportUrl({ ...base, range: 'day' })).toBe('/api/v2/export/pdf/day?date=2026-09-16');
    expect(exportUrl({ ...base, range: 'due' })).toBe('/api/v2/export/pdf/due');
  });

  it('passes English through to every PDF endpoint', () => {
    expect(exportUrl({ ...base, range: '2', language: 'en' })).toContain('language=en');
    expect(exportUrl({ ...base, range: 'day', language: 'en' })).toBe(
      '/api/v2/export/pdf/day?date=2026-09-16&language=en',
    );
    expect(exportUrl({ ...base, range: 'due', language: 'en' })).toBe(
      '/api/v2/export/pdf/due?language=en',
    );
  });
});

describe('tasksPdfUrl', () => {
  it('addresses the task list sheet, in English on request', () => {
    expect(tasksPdfUrl()).toBe('/api/v2/export/pdf/tasks');
    expect(tasksPdfUrl('nl')).toBe('/api/v2/export/pdf/tasks');
    expect(tasksPdfUrl('en')).toBe('/api/v2/export/pdf/tasks?language=en');
  });
});
