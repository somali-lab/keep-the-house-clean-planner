import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiV2, toOccurrence, unwrap } from '../../api/index.ts';

export interface CompletionEdit {
  id: string;
  date: string;
  completedAt: string;
  completedBy: string;
}

/** One page of done occurrences: the list can grow for years, so it is loaded page by page (a Load more button). */
export const COMPLETIONS_PAGE_SIZE = 100;

/** The done occurrences of the days from `from` to `to`, newest day first (`order=desc`, kept on every page), one page at a time. */
export function useCompletionRecords(from: string, to: string) {
  return useInfiniteQuery({
    queryKey: ['occurrences', 'completed', from, to],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) => {
      const { data } = await unwrap(
        apiV2.GET('/api/v2/occurrences', {
          params: { query: { from, to, status: 'done', limit: String(COMPLETIONS_PAGE_SIZE), order: 'desc', ...(pageParam ? { cursor: pageParam } : {}) } },
        }),
      );
      return { items: data.items.map(toOccurrence), nextCursor: data.nextCursor };
    },
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
  });
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
