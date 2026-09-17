import type { CyclePlan, PlanSummary, PlanWarning, Slot } from '@huishoudplanner/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

export const planKeys = {
  all: ['cycle-plans'] as const,
};

export interface PutSlotsResponse {
  plan: CyclePlan;
  warnings: PlanWarning[];
  summary: PlanSummary;
}

export function usePlans() {
  return useQuery({
    queryKey: planKeys.all,
    queryFn: async () => (await api.get<CyclePlan[]>('/api/cycle-plans')).data,
  });
}

function replacePlan(plans: CyclePlan[] | undefined, plan: CyclePlan): CyclePlan[] | undefined {
  return plans?.map((p) => (p._id === plan._id ? plan : p));
}

export function usePutSlots() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ planId, slots }: { planId: string; slots: Slot[] }) =>
      (await api.put<PutSlotsResponse>(`/api/cycle-plans/${planId}/slots?sync=true`, { slots }))
        .data,
    onSuccess: (data) => {
      queryClient.setQueryData<CyclePlan[]>(planKeys.all, (plans) => replacePlan(plans, data.plan));
      if (data.plan.active) {
        void queryClient.invalidateQueries({ queryKey: ['occurrences'] });
        void queryClient.invalidateQueries({ queryKey: ['due'] });
      }
    },
  });
}

export function useUpdatePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      planId,
      patch,
    }: {
      planId: string;
      patch: { name?: string; weekThemes?: string[] };
    }) => (await api.patch<CyclePlan>(`/api/cycle-plans/${planId}`, patch)).data,
    onSuccess: (plan) => {
      queryClient.setQueryData<CyclePlan[]>(planKeys.all, (plans) => replacePlan(plans, plan));
    },
  });
}

export function useCreatePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: { name: string; copyFromId?: string }) =>
      (await api.post<CyclePlan>('/api/cycle-plans', input)).data,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: planKeys.all }),
  });
}

export function useActivatePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (planId: string) =>
      (await api.post<{ plan: CyclePlan; removed: number }>(`/api/cycle-plans/${planId}/activate`))
        .data,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: planKeys.all }),
  });
}

export function useDeletePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (planId: string) =>
      (await api.delete<{ deleted: true }>(`/api/cycle-plans/${planId}`)).data,
    onSuccess: (_data, planId) => {
      queryClient.setQueryData<CyclePlan[]>(planKeys.all, (plans) =>
        plans?.filter((plan) => plan._id !== planId),
      );
    },
  });
}
