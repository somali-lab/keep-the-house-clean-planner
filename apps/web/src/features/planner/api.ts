import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiV2, isStaleEntity, removeFromList, replaceInList, unwrap } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';
import { ifMatch } from '../../api/v2/concurrency.ts';
import { collectPages } from '../../api/v2/paging.ts';
import type { components } from '../../api/v2/schema';

type Schemas = components['schemas'];

/** A placement of a task in a plan: `weekday` 0 (Sunday) to 6 (Saturday), `assigneeId` null means anyone. */
export interface PlanSlot {
  taskId: string;
  weekIndex: number;
  weekday: number;
  assigneeId: string | null;
  sortOrder: number;
}

/** A cycle plan as `GET /api/v2/cycle-plans` answers it. */
export interface CyclePlan {
  id: string;
  name: string;
  active: boolean;
  slots: PlanSlot[];
  weekThemes: string[];
  draft: boolean;
  source: string;
  proposalId: string | null;
  rationale: string[] | null;
  discarded: boolean;
  createdAt: string;
  updatedAt: string;
  /** The version of the document: the ETag of a write on it is `"<version>"` (ADR-0022). */
  version: number;
}

/** A hard error or a warning of the server's plan validation; only the members that belong to its code are set. */
export interface PlanIssue {
  code: string;
  slotIndex: number | null;
  taskId: string | null;
  userId: string | null;
  weekIndex: number | null;
  weekday: number | null;
  period: string | null;
  placed: number | null;
  required: number | null;
  minutes: number | null;
  budget: number | null;
}

export interface TaskSummary {
  taskId: string;
  placed: number;
  /** Null when the interval is not planned through the grid (for example a quarter). */
  required: number | null;
}

export interface DaySummary {
  weekIndex: number;
  weekday: number;
  users: { userId: string; minutes: number; budget: number; overBudget: boolean }[];
  unassignedMinutes: number;
}

export interface WeekSummary {
  weekIndex: number;
  users: { userId: string; minutes: number }[];
  unassignedMinutes: number;
}

/** The workload of a plan as the server computes it. */
export interface PlanSummary {
  tasks: TaskSummary[];
  days: DaySummary[];
  weeks: WeekSummary[];
}

/** What a save of the slots would answer: the same errors, warnings and summary (`POST .../validation`). */
export interface PlanValidation {
  valid: boolean;
  errors: Record<string, string[]>;
  issues: PlanIssue[];
  warnings: PlanIssue[];
  summary: PlanSummary;
}

export const EMPTY_SUMMARY: PlanSummary = { tasks: [], days: [], weeks: [] };

const nullableInt = (value: number | string | null | undefined): number | null =>
  value === null || value === undefined ? null : toInt(value);

export const toSlot = (slot: Schemas['PlanSlotResponse']): PlanSlot => ({
  taskId: slot.taskId,
  weekIndex: toInt(slot.weekIndex),
  weekday: toInt(slot.weekday),
  assigneeId: slot.assigneeId,
  sortOrder: toInt(slot.sortOrder),
});

export const toPlan = (plan: Schemas['CyclePlanResponse']): CyclePlan => ({
  ...plan,
  slots: plan.slots.map(toSlot),
  version: toInt(plan.version),
});

export const toIssue = (issue: Schemas['PlanIssueResponse']): PlanIssue => ({
  code: issue.code,
  slotIndex: nullableInt(issue.slotIndex),
  taskId: issue.taskId,
  userId: issue.userId,
  weekIndex: nullableInt(issue.weekIndex),
  weekday: nullableInt(issue.weekday),
  period: issue.period,
  placed: nullableInt(issue.placed),
  required: nullableInt(issue.required),
  minutes: nullableInt(issue.minutes),
  budget: nullableInt(issue.budget),
});

export const toSummary = (summary: Schemas['PlanSummaryResponse']): PlanSummary => ({
  tasks: summary.tasks.map((task) => ({ taskId: task.taskId, placed: toInt(task.placed), required: nullableInt(task.required) })),
  days: summary.days.map((day) => ({
    weekIndex: toInt(day.weekIndex),
    weekday: toInt(day.weekday),
    users: day.users.map((user) => ({ userId: user.userId, minutes: toInt(user.minutes), budget: toInt(user.budget), overBudget: user.overBudget })),
    unassignedMinutes: toInt(day.unassignedMinutes),
  })),
  weeks: summary.weeks.map((week) => ({
    weekIndex: toInt(week.weekIndex),
    users: week.users.map((user) => ({ userId: user.userId, minutes: toInt(user.minutes) })),
    unassignedMinutes: toInt(week.unassignedMinutes),
  })),
});

export const toValidation = (validation: Schemas['PlanValidationResponse']): PlanValidation => ({
  valid: validation.valid,
  errors: validation.errors,
  issues: validation.issues.map(toIssue),
  warnings: validation.warnings.map(toIssue),
  summary: toSummary(validation.summary),
});

/** The slots as a request body carries them: all five members, `assigneeId` null meaning anyone. */
export const slotsBody = (slots: readonly PlanSlot[]): Schemas['PlanSlotBody'][] =>
  slots.map((slot) => ({
    taskId: slot.taskId,
    weekIndex: slot.weekIndex,
    weekday: slot.weekday,
    assigneeId: slot.assigneeId,
    sortOrder: slot.sortOrder,
  }));

/** The most the server returns in one page of plans (`limit` 1 to 200). */
const LIST_PAGE_SIZE = 200;
// The key starts with `cycle-plans`, so an invalidation of `['cycle-plans']` (tasks, activation) refreshes the plans of every page.
// invalidation of `['cycle-plans']` (tasks, activation) refreshes both.
export const planKeys = {
  all: ['cycle-plans', 'v2'] as const,
};

export async function fetchPlans(): Promise<CyclePlan[]> {
  const plans = await collectPages(async (cursor) =>
    (await unwrap(apiV2.GET('/api/v2/cycle-plans', { params: { query: { limit: String(LIST_PAGE_SIZE), cursor } } }))).data,
  );
  return plans.map(toPlan);
}

export function usePlans() {
  return useQuery({ queryKey: planKeys.all, queryFn: fetchPlans });
}

/** What an activation changes, as `GET /api/v2/cycle-plans/{id}/activation-preview` answers it. */
export interface ActivationPreviewItem {
  occurrenceId: string | null;
  taskId: string | null;
  taskName: string;
  date: string;
  assigneeId: string | null;
}

export interface ActivationPreview {
  planId: string;
  previewToken: string;
  asOfDate: string;
  removed: ActivationPreviewItem[];
  added: ActivationPreviewItem[];
  preserved: Record<'done' | 'skipped' | 'moved' | 'adhoc', ActivationPreviewItem[]>;
}

export function useActivationPreview(planId: string | null, enabled: boolean) {
  return useQuery({
    queryKey: [...planKeys.all, planId, 'activation-preview'],
    queryFn: async (): Promise<ActivationPreview> => {
      const { data } = await unwrap(
        apiV2.GET('/api/v2/cycle-plans/{id}/activation-preview', { params: { path: { id: planId! } } }),
      );
      return data as unknown as ActivationPreview;
    },
    enabled: Boolean(planId && enabled),
    staleTime: 0,
  });
}

/**
 * The validation of the slots of the editor, by the server: the stored plan when the slots are what is stored, the unsaved draft otherwise.
 * The previous answer stays on screen while the next one is read.
 */
export function usePlanValidation(planId: string, slots: readonly PlanSlot[], stored: boolean) {
  const body = slotsBody(slots);
  return useQuery({
    queryKey: ['plan-validation', planId, stored ? 'stored' : JSON.stringify(body)],
    queryFn: async (): Promise<PlanValidation> => {
      const answer = stored
        ? await unwrap(apiV2.POST('/api/v2/cycle-plans/{id}/validation', { params: { path: { id: planId } } }))
        : await unwrap(apiV2.POST('/api/v2/cycle-plans/validation', { body: { slots: body } }));
      return toValidation(answer.data);
    },
    placeholderData: keepPreviousData,
    staleTime: 0,
  });
}

export interface PutSlotsResult {
  plan: CyclePlan;
  warnings: PlanIssue[];
  summary: PlanSummary;
}

/** A write on a plan carries the version of the plan the person was editing (ADR-0022). */
export interface PlanWrite {
  planId: string;
  version: number;
}

export function usePutSlots() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ planId, version, slots }: PlanWrite & { slots: readonly PlanSlot[] }): Promise<PutSlotsResult> => {
      const { data } = await unwrap(
        apiV2.PUT('/api/v2/cycle-plans/{id}/slots', {
          params: { path: { id: planId }, header: ifMatch({ version }) },
          body: { slots: slotsBody(slots) },
        }),
      );
      return { plan: toPlan(data.plan), warnings: data.warnings.map(toIssue), summary: toSummary(data.summary) };
    },
    onSuccess: (data) => {
      replaceInList(queryClient, planKeys.all, data.plan);
      void queryClient.invalidateQueries({ queryKey: ['plan-validation', data.plan.id] });
      if (data.plan.active) {
        void queryClient.invalidateQueries({ queryKey: ['occurrences'] });
        void queryClient.invalidateQueries({ queryKey: ['due'] });
      }
    },
    // A stale write wrote nothing: read the plans again, so that the next save carries the stored version.
    onError: async (error) => {
      if (isStaleEntity(error)) await queryClient.invalidateQueries({ queryKey: planKeys.all });
    },
  });
}

/** `weekThemes` is always the four of the plan; `name` is trimmed by the caller. */
export type PlanPatch = { name: string } | { weekThemes: string[] };

export function useUpdatePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ planId, version, patch }: PlanWrite & { patch: PlanPatch }): Promise<CyclePlan> => {
      const { data } = await unwrap(
        apiV2.PATCH('/api/v2/cycle-plans/{id}', {
          params: { path: { id: planId }, header: ifMatch({ version }) },
          // The generated type lists every member as required, the API accepts any of them.
          body: patch as unknown as Schemas['UpdateCyclePlanRequest'],
        }),
      );
      return toPlan(data);
    },
    onSuccess: (plan) => replaceInList(queryClient, planKeys.all, plan),
    onError: async (error) => {
      if (isStaleEntity(error)) await queryClient.invalidateQueries({ queryKey: planKeys.all });
    },
  });
}

export function useCreatePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: { name: string; copyFromId?: string }): Promise<CyclePlan> => {
      // `copyFromId` is left out for an empty plan: the parser refuses an explicit null.
      const body = (input.copyFromId ? { name: input.name, copyFromId: input.copyFromId } : { name: input.name }) as Schemas['CreateCyclePlanRequest'];
      return toPlan((await unwrap(apiV2.POST('/api/v2/cycle-plans', { body }))).data);
    },
    onSuccess: (created) => {
      queryClient.setQueryData<CyclePlan[]>(planKeys.all, (plans) => (plans ? [...plans, created] : [created]));
    },
  });
}

export function useActivatePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ planId, previewToken }: { planId: string; previewToken: string }) =>
      (await unwrap(apiV2.POST('/api/v2/cycle-plans/{id}/activation', { params: { path: { id: planId } }, body: { previewToken } }))).data,
    onSuccess: async () => {
      await Promise.all(
        [planKeys.all, ['occurrences'], ['due']].map((queryKey) => queryClient.invalidateQueries({ queryKey })),
      );
    },
  });
}

export function useDeletePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ planId, version }: PlanWrite) =>
      (await unwrap(apiV2.DELETE('/api/v2/cycle-plans/{id}', { params: { path: { id: planId }, header: ifMatch({ version }) } }))).data,
    onSuccess: (_deleted, { planId }) => removeFromList<CyclePlan>(queryClient, planKeys.all, planId),
    onError: async (error) => {
      if (isStaleEntity(error)) await queryClient.invalidateQueries({ queryKey: planKeys.all });
    },
  });
}
