import { useQuery } from '@tanstack/react-query';
import { toInt } from '../occurrence.ts';
import { apiV2 } from '../index.ts';
import { unwrap } from './client.ts';

/** The limits and defaults the web app needs from the server (`GET /api/v2/meta/limits`). */
export interface Limits {
  calendar: { maxRangeDays: number };
  tasks: {
    minPoints: number;
    maxPoints: number;
    minDurationMinutes: number;
    oneOffNameMaxLength: number;
    skipReasonMaxLength: number;
  };
}

export const limitsKey = ['limits'] as const;

/** Read once per session: the values never change while the server runs. */
export function useLimits() {
  return useQuery({
    queryKey: limitsKey,
    staleTime: Infinity,
    gcTime: Infinity,
    queryFn: async (): Promise<Limits> => {
      const { data } = await unwrap(apiV2.GET('/api/v2/meta/limits'));
      const tasks = data.tasks;
      return {
        calendar: { maxRangeDays: toInt(data.calendar?.maxRangeDays ?? 371) },
        tasks: {
          minPoints: toInt(tasks?.minPoints ?? 0),
          maxPoints: toInt(tasks?.maxPoints ?? 1000),
          minDurationMinutes: toInt(tasks?.minDurationMinutes ?? 1),
          oneOffNameMaxLength: toInt(tasks?.oneOffNameMaxLength ?? 120),
          skipReasonMaxLength: toInt(tasks?.skipReasonMaxLength ?? 500),
        },
      };
    },
  });
}

/** Where a day falls in the cycles, as the server counts it. */
export interface CalendarDay {
  dayKey: string;
  /** 0 = Sunday .. 6 = Saturday. */
  weekday: number;
  /** Negative before the cycle anchor. */
  cycleIndex: number;
  /** The week of the cycle, 0 to 3. */
  weekIndex: number;
  isoWeek: string;
  weekStart: string;
}

export const calendarKey = (from: string, to: string) => ['calendar', from, to] as const;

/** Cycle index, week index and ISO week of every day from `from` to `to` (both included). */
export function useCalendar(from: string, to: string, enabled = true) {
  return useQuery({
    queryKey: calendarKey(from, to),
    enabled,
    queryFn: async (): Promise<Map<string, CalendarDay>> => {
      const { data } = await unwrap(apiV2.GET('/api/v2/calendar', { params: { query: { from, to } } }));
      return new Map(
        data.days.map((day) => [
          day.dayKey,
          {
            dayKey: day.dayKey,
            weekday: toInt(day.weekday),
            cycleIndex: toInt(day.cycleIndex),
            weekIndex: toInt(day.weekIndex),
            isoWeek: day.isoWeek,
            weekStart: day.weekStart,
          },
        ]),
      );
    },
  });
}
