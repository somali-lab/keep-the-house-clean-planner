import { useInfiniteQuery, useMutation, useQueries, useQueryClient } from '@tanstack/react-query';
import { useMemo } from 'react';
import { apiV2, unwrap } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';
import { calendarKey, fetchCalendar, useLimits } from '../../api/v2/queries.ts';
import { isoWeekNumber } from '../stats/pointsModel.ts';
import {
  auditQuery,
  bonusPeriodStarts,
  calendarRanges,
  toAuditEntry,
  type AuditEntry,
  type AuditFilters,
} from './auditModel.ts';
import type { BonusKeyContext } from './bonusKey.ts';

export { AUDIT_PAGE_SIZE, type AuditFilters } from './auditModel.ts';

/** The history, newest first, one page at a time (`GET /api/v2/audit`; reading needs no profile). */
export function useAuditFeed(filters: AuditFilters) {
  return useInfiniteQuery({
    queryKey: ['audit', filters],
    initialPageParam: null as string | null,
    queryFn: async ({ pageParam }) => {
      const { data } = await unwrap(apiV2.GET('/api/v2/audit', { params: { query: auditQuery(filters, pageParam) } }));
      return { items: data.items.map(toAuditEntry), nextCursor: data.nextCursor };
    },
    getNextPageParam: (last) => last.nextCursor,
  });
}

/** Clears the whole history (administrators; there is no single entity to version, so no `If-Match`). */
export function useClearAudit() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => toInt((await unwrap(apiV2.DELETE('/api/v2/audit'))).data.deleted),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['audit'] }),
  });
}

/** The week numbers of the week bonuses that the loaded entries name come from the server calendar, one request per stretch of days. */
export function useBonusKeyContext(entries: readonly AuditEntry[]): BonusKeyContext {
  const limits = useLimits();
  const maxRangeDays = limits.data?.calendar.maxRangeDays;
  const ranges = useMemo(
    () => (maxRangeDays === undefined ? [] : calendarRanges(bonusPeriodStarts(entries), maxRangeDays)),
    [entries, maxRangeDays],
  );
  const weeks = useQueries({
    queries: ranges.map(({ from, to }) => ({ queryKey: calendarKey(from, to), queryFn: () => fetchCalendar(from, to) })),
    combine: (results) => results.map((result) => result.data),
  });
  return useMemo(() => {
    const byDay = new Map<string, number>();
    for (const days of weeks) {
      for (const day of days?.values() ?? []) {
        const week = isoWeekNumber(day.isoWeek);
        if (week !== null) byDay.set(day.dayKey, week);
      }
    }
    return { weekOf: (dayKey: string) => byDay.get(dayKey) ?? null, cycleDays: limits.data?.calendar.cycleDays };
  }, [weeks, limits.data?.calendar.cycleDays]);
}
