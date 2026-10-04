import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiV2, unwrap } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';
import type { components } from '../../api/v2/schema';
import { planKeys } from '../planner/api.ts';

type Schemas = components['schemas'];

/** "You keep moving this task from one day to another": the server's suggestion to change the plan. */
export interface PromoteSuggestion {
  planId: string;
  taskId: string;
  taskName: string;
  fromSlot: { weekIndex: number; weekday: number; assigneeId: string | null };
  toWeekday: number;
  /** Set only when every move also went to the same other person. */
  toAssigneeId: string | null;
  /** The moved occurrences, newest first; the first is the newest evidence. */
  evidence: string[];
}

export const promoteKeys = { all: ['promote-suggestions'] as const };

const toSuggestion = (s: Schemas['PromoteSuggestionResponse']): PromoteSuggestion => ({
  planId: s.planId,
  taskId: s.taskId,
  taskName: s.taskName,
  fromSlot: { weekIndex: toInt(s.fromSlot.weekIndex), weekday: toInt(s.fromSlot.weekday), assigneeId: s.fromSlot.assigneeId },
  toWeekday: toInt(s.toWeekday),
  toAssigneeId: s.toAssigneeId,
  evidence: s.evidence,
});

export function usePromoteSuggestions() {
  return useQuery({
    queryKey: promoteKeys.all,
    queryFn: async () => (await unwrap(apiV2.GET('/api/v2/promote-suggestions'))).data.items.map(toSuggestion),
  });
}

export function usePromoteActions() {
  const queryClient = useQueryClient();
  // A promotion changes the plan (and the occurrences of an active one), so both are read again.
  const refresh = () =>
    Promise.all(
      [promoteKeys.all, planKeys.all, ['cycle-plans'], ['occurrences'], ['due']].map((queryKey) =>
        queryClient.invalidateQueries({ queryKey }),
      ),
    );

  const apply = useMutation({
    mutationFn: async (s: PromoteSuggestion) =>
      (
        await unwrap(
          apiV2.POST('/api/v2/promote-suggestions/apply', {
            // `toAssigneeId` is left out for "no change of person": the parser refuses an explicit null here.
            body: {
              planId: s.planId,
              taskId: s.taskId,
              weekIndex: s.fromSlot.weekIndex,
              weekday: s.fromSlot.weekday,
              toWeekday: s.toWeekday,
              ...(s.toAssigneeId ? { toAssigneeId: s.toAssigneeId } : {}),
            } as Schemas['ApplyPromotionRequest'],
          }),
        )
      ).data,
    onSuccess: refresh,
  });

  const dismiss = useMutation({
    mutationFn: async (s: PromoteSuggestion) =>
      (
        await unwrap(
          apiV2.POST('/api/v2/promote-suggestions/dismiss', {
            // Here the API documents null as meaningful: the suggestion had no other person.
            body: {
              planId: s.planId,
              taskId: s.taskId,
              weekIndex: s.fromSlot.weekIndex,
              weekday: s.fromSlot.weekday,
              toWeekday: s.toWeekday,
              toAssigneeId: s.toAssigneeId,
              lastEvidenceId: s.evidence[0]!,
            },
          }),
        )
      ).data,
    onSuccess: refresh,
  });

  return { apply, dismiss };
}
