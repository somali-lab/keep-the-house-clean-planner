import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiV2, ifMatch, isStaleEntity, removeFromList, replaceInList, unwrap } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';
import { collectPages } from '../../api/v2/paging.ts';
import type { components } from '../../api/v2/schema';
import type { Language } from '../../i18n/runtime.ts';

export const BADGE_RULE_TYPES = ['executions', 'minutes', 'onTimeWeeks'] as const;
export type BadgeRuleType = (typeof BADGE_RULE_TYPES)[number];

export interface BadgeRule {
  type: BadgeRuleType;
  /** The tasks the rule counts; empty means every task. Always empty for `onTimeWeeks`. */
  taskIds: string[];
  threshold: number;
}

/** What the server says about the stored picture of a badge; `url` carries `?v=<start of the hash>` and so can be cached for good. */
export interface BadgeImage {
  contentType: string;
  size: number;
  hash: string;
  url: string;
}

/** A badge as `GET /api/v2/badges` answers it (ADR-0014); `version` is the validator of `If-Match` (ADR-0022). */
export interface Badge {
  id: string;
  name: string;
  description: string;
  rule: BadgeRule;
  active: boolean;
  exampleKey: string | null;
  image: BadgeImage | null;
  createdAt: string;
  updatedAt: string;
  version: number;
}

/** How far one person is towards one badge; `awardedAt` is set once the badge is earned. */
export interface BadgeProgressItem {
  badgeId: string;
  current: number;
  threshold: number;
  awardedAt: string | null;
}

type BadgeResponse = components['schemas']['BadgeResponse'];
type CreateBadgeRequest = components['schemas']['CreateBadgeRequest'];
type UpdateBadgeRequest = components['schemas']['UpdateBadgeRequest'];

/** The body of a new badge. Optional keys are left out, never sent as `null`: the API refuses an explicit null there. */
export interface CreateBadgeBody {
  name: string;
  description: string;
  rule: { type: BadgeRuleType; taskIds?: string[]; threshold: number };
  active: boolean;
  image?: { contentType: string; data: string };
}

/** The body of a change: `image` is left out to keep the picture, an object replaces it and `null` removes it. */
export interface UpdateBadgeBody extends Omit<CreateBadgeBody, 'image'> {
  image?: { contentType: string; data: string } | null;
}

export function toBadge(raw: BadgeResponse): Badge {
  return {
    id: raw.id,
    name: raw.name,
    description: raw.description,
    rule: { type: raw.rule.type as BadgeRuleType, taskIds: raw.rule.taskIds ?? [], threshold: toInt(raw.rule.threshold) },
    active: raw.active,
    exampleKey: raw.exampleKey,
    image: raw.image ? { contentType: raw.image.contentType, size: toInt(raw.image.size), hash: raw.image.hash, url: raw.image.url } : null,
    createdAt: raw.createdAt,
    updatedAt: raw.updatedAt,
    version: toInt(raw.version),
  };
}

/** Every badge query shares the `badges` prefix, so a check-off can refresh them all at once. */
export const badgeKeys = {
  all: ['badges'] as const,
  list: ['badges', 'list'] as const,
  progress: (personId: string | null) => ['badges', 'progress', personId] as const,
};

/** The most the server returns in one page of badges (`limit` 1 to 100). */
const LIST_PAGE_SIZE = 100;

export async function fetchBadges(): Promise<Badge[]> {
  const badges = await collectPages(async (cursor) =>
    (await unwrap(apiV2.GET('/api/v2/badges', { params: { query: { limit: String(LIST_PAGE_SIZE), cursor } } }))).data,
  );
  return badges.map(toBadge);
}

/** All badges, inactive ones included; the page of the administrator needs them. */
export function useBadges() {
  return useQuery({ queryKey: badgeKeys.list, queryFn: fetchBadges });
}

/** How far one person is towards every active badge; `awardedAt` says which ones are earned (ADR-0014). */
export function useBadgeProgress(personId: string | null) {
  return useQuery({
    queryKey: badgeKeys.progress(personId),
    queryFn: async () => {
      const { data } = await unwrap(apiV2.GET('/api/v2/badges/progress', { params: { query: { personId: personId! } } }));
      return {
        personId: data.personId,
        items: data.items.map(
          (item): BadgeProgressItem => ({
            badgeId: item.badgeId,
            current: toInt(item.current),
            threshold: toInt(item.threshold),
            awardedAt: item.awardedAt,
          }),
        ),
      };
    },
    enabled: personId !== null,
  });
}

/**
 * Creates a badge, or changes `badge` with the version the person read (`If-Match`, ADR-0022). A stale version (412) wrote nothing: the
 * list is read again so that the next save carries the stored version; the editor keeps what the person typed.
 */
export function useSaveBadge() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: { badge?: Badge; create: CreateBadgeBody; patch: UpdateBadgeBody }): Promise<Badge> => {
      // The generated types list every key as required (nullable); the API wants the optional ones left out.
      if (input.badge) {
        const { data } = await unwrap(
          apiV2.PATCH('/api/v2/badges/{id}', {
            params: { path: { id: input.badge.id }, header: ifMatch(input.badge) },
            body: input.patch as unknown as UpdateBadgeRequest,
          }),
        );
        return toBadge(data);
      }
      return toBadge((await unwrap(apiV2.POST('/api/v2/badges', { body: input.create as unknown as CreateBadgeRequest }))).data);
    },
    onSuccess: (saved, { badge }) => {
      if (badge) replaceInList(queryClient, badgeKeys.list, saved);
    },
    onSettled: (_saved, error) => queryClient.invalidateQueries({ queryKey: isStaleEntity(error) ? badgeKeys.list : badgeKeys.all }),
  });
}

export function useDeleteBadge() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (badge: Badge) =>
      (await unwrap(apiV2.DELETE('/api/v2/badges/{id}', { params: { path: { id: badge.id }, header: ifMatch(badge) } }))).data,
    onSuccess: (_deleted, badge) => removeFromList<Badge>(queryClient, badgeKeys.list, badge.id),
    onSettled: (_deleted, error) => queryClient.invalidateQueries({ queryKey: isStaleEntity(error) ? badgeKeys.list : badgeKeys.all }),
  });
}

/** Adds the example badges that do not exist yet; calling it again changes nothing. */
export function useAddExampleBadges() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (language: Language) => {
      const { data } = await unwrap(apiV2.POST('/api/v2/badges/examples', { body: { language } }));
      return { created: data.created.map(toBadge), skipped: toInt(data.skipped) };
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: badgeKeys.all }),
  });
}
