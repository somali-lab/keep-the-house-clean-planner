import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { apiV2, ifMatch, isStaleEntity, replaceInList, toOccurrence, unwrap, type Occurrence } from '../../api/index.ts';
import { toUser, usersKey, type User } from '../../api/v2/household.ts';
import { collectPages } from '../../api/v2/paging.ts';
import { addDays } from '@/lib/dayKey';
import { OVERDUE_LOOKBACK_DAYS } from '../today/todayModel.ts';

/** The most the server returns in one page of occurrences (`limit` 1 to 500). */
const PAGE_SIZE = 500;

/** The person's open occurrences for today and the overdue lookback window, fetched fresh at delivery time. */
export function fetchOpenOccurrences(queryClient: QueryClient, personId: string, todayKey: string): Promise<Occurrence[]> {
  const from = addDays(todayKey, -OVERDUE_LOOKBACK_DAYS);
  return queryClient.fetchQuery({
    // Starts with 'occurrences' so every occurrence mutation also invalidates it.
    queryKey: ['occurrences', 'notify', personId, from, todayKey],
    queryFn: async () =>
      (
        await collectPages(async (cursor) =>
          (
            await unwrap(
              apiV2.GET('/api/v2/occurrences', {
                params: { query: { from, to: todayKey, status: 'open', assigneeId: personId, limit: String(PAGE_SIZE), cursor } },
              }),
            )
          ).data,
        )
      ).map(toOccurrence),
    staleTime: 0,
  });
}

/**
 * Saves a person's moments (their own, or anyone's for an administrator). The moments are a field of the person, so the write carries
 * the version of the person as `If-Match` (ADR-0022); the answer goes back into the people list. A stale version (412) reads the
 * people again and leaves the person's edit in its form.
 */
export function useSaveBrowserNotifications() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ user, settings }: { user: User; settings: { enabled: boolean; times: string[] } }): Promise<User> =>
      toUser(
        (
          await unwrap(
            apiV2.PUT('/api/v2/users/{id}/browser-notifications', {
              params: { path: { id: user.id }, header: ifMatch(user) },
              body: settings,
            }),
          )
        ).data,
      ),
    onSuccess: async (saved) => {
      replaceInList(queryClient, usersKey, saved);
      await queryClient.invalidateQueries({ queryKey: usersKey });
    },
    onError: async (error) => {
      if (isStaleEntity(error)) await queryClient.invalidateQueries({ queryKey: usersKey });
    },
  });
}
