import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiV2, toOccurrence, unwrap, type Occurrence } from '../../api/index.ts';
import { collectPages } from '../../api/v2/paging.ts';

export interface CompletionEdit {
  id: string;
  date: string;
  completedAt: string;
  completedBy: string;
}

/** The most the server returns in one page of occurrences (`limit` 1 to 500). */
const PAGE_SIZE = 500;

/** The done occurrences of the days from `from` to `to`, oldest day first, all pages. */
export async function fetchCompletionRecords(from: string, to: string): Promise<Occurrence[]> {
  const items = await collectPages(async (cursor) =>
    (await unwrap(apiV2.GET('/api/v2/occurrences', { params: { query: { from, to, status: 'done', limit: String(PAGE_SIZE), cursor } } }))).data,
  );
  return items.map(toOccurrence);
}

export function useCompletionRecords(from: string, to: string) {
  return useQuery({ queryKey: ['occurrences', 'completed', from, to], queryFn: () => fetchCompletionRecords(from, to) });
}

function useRefreshCompletionData() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries();
}

/** Corrects a recorded completion (administrators): an intent endpoint, so it carries no `If-Match`. */
export function useEditCompletion() {
  const refresh = useRefreshCompletionData();
  return useMutation({
    mutationFn: async ({ id, date, completedAt, completedBy }: CompletionEdit) =>
      toOccurrence(
        (await unwrap(apiV2.POST('/api/v2/occurrences/{id}/completion', { params: { path: { id } }, body: { date, completedAt, completedBy } }))).data,
      ),
    onSuccess: refresh,
  });
}

/** Deletes a done occurrence for good (administrators); no `If-Match`. */
export function useDeleteCompletion() {
  const refresh = useRefreshCompletionData();
  return useMutation({
    mutationFn: async (id: string) => (await unwrap(apiV2.DELETE('/api/v2/occurrences/{id}', { params: { path: { id } } }))).data,
    onSuccess: refresh,
  });
}
