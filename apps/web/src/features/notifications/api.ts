import type { BrowserNotifications, OccurrenceView, User } from '@huishoudplanner/shared';
import { addDays } from '@huishoudplanner/shared/time';
import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';
import { USERS_QUERY_KEY } from '../../identity/index.ts';
import { OVERDUE_LOOKBACK_DAYS } from '../today/todayModel.ts';

/** The person's open occurrences for today and the overdue lookback window, fetched fresh at delivery time. */
export function fetchOpenOccurrences(queryClient: QueryClient, personId: string, todayKey: string): Promise<OccurrenceView[]> {
  const from = addDays(todayKey, -OVERDUE_LOOKBACK_DAYS);
  return queryClient.fetchQuery({
    // Starts with 'occurrences' so every occurrence mutation also invalidates it.
    queryKey: ['occurrences', 'notify', personId, from, todayKey],
    queryFn: async () =>
      (await api.get<OccurrenceView[]>(`/api/occurrences?from=${from}&to=${todayKey}&status=open&assigneeId=${personId}`)).data,
    staleTime: 0,
  });
}

/** Saves a person's moments (their own, or anyone's for an administrator) and refreshes the people list. */
export function useSaveBrowserNotifications() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: { userId: string; settings: BrowserNotifications }) =>
      (await api.put<User>(`/api/users/${input.userId}/browser-notifications`, input.settings)).data,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: USERS_QUERY_KEY }),
  });
}
