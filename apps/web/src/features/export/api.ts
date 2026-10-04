import { useQuery } from '@tanstack/react-query';
import { apiV2, unwrap } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';
import { collectPages } from '../../api/v2/paging.ts';

/** A generated cycle as `GET /api/v2/cycles` answers it: 28 days from a Monday. */
export interface Cycle {
  id: string;
  /** 0 starts on the anchor date; negative before it. */
  index: number;
  startDate: string;
  endDate: string;
  planId: string | null;
  generatedAt: string;
  generationRunId: string;
}

/** The most the server returns in one page of cycles (`limit` 1 to 200). */
const PAGE_SIZE = 200;

export const cyclesKey = ['cycles'] as const;

export async function fetchCycles(): Promise<Cycle[]> {
  const cycles = await collectPages(async (cursor) =>
    (await unwrap(apiV2.GET('/api/v2/cycles', { params: { query: { limit: String(PAGE_SIZE), cursor } } }))).data,
  );
  return cycles.map((cycle) => ({ ...cycle, index: toInt(cycle.index) }));
}

/** The generated cycles: the weeks a PDF sheet can be made of. */
export function useCycles() {
  return useQuery({ queryKey: cyclesKey, queryFn: fetchCycles });
}
