import type {
  AddExampleBadgesResponse,
  Badge,
  BadgeProgressResponse,
  BadgesResponse,
  CreateBadgeInput,
  UpdateBadgeInput,
} from '@huishoudplanner/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';
import type { Language } from '../../i18n/runtime.ts';

/** Every badge query shares the `badges` prefix, so a check-off can refresh them all at once. */
export const badgeKeys = {
  all: ['badges'] as const,
  list: ['badges', 'list'] as const,
  progress: (personId: string | null) => ['badges', 'progress', personId] as const,
};

/** All badges, inactive ones included; the page of the administrator needs them. */
export function useBadges() {
  return useQuery({
    queryKey: badgeKeys.list,
    queryFn: async () => (await api.get<BadgesResponse>('/api/badges')).data.badges,
  });
}

/** How far one person is towards every active badge; `awardedAt` says which ones are earned (ADR-0014). */
export function useBadgeProgress(personId: string | null) {
  return useQuery({
    queryKey: badgeKeys.progress(personId),
    queryFn: async () => (await api.get<BadgeProgressResponse>(`/api/badges/progress?personId=${personId}`)).data,
    enabled: personId !== null,
  });
}

export function useSaveBadge() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: { id: string | null; create: CreateBadgeInput; patch: UpdateBadgeInput }) =>
      input.id
        ? (await api.patch<Badge>(`/api/badges/${input.id}`, input.patch)).data
        : (await api.post<Badge>('/api/badges', input.create)).data,
    onSettled: () => queryClient.invalidateQueries({ queryKey: badgeKeys.all }),
  });
}

export function useDeleteBadge() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => (await api.delete<{ deleted: boolean }>(`/api/badges/${id}`)).data,
    onSettled: () => queryClient.invalidateQueries({ queryKey: badgeKeys.all }),
  });
}

/** Adds the example badges that do not exist yet; calling it again changes nothing. */
export function useAddExampleBadges() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (language: Language) => (await api.post<AddExampleBadgesResponse>('/api/badges/examples', { language })).data,
    onSettled: () => queryClient.invalidateQueries({ queryKey: badgeKeys.all }),
  });
}
