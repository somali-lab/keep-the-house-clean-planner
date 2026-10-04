import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiV2, unwrap } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';
import { releaseRequestKey, requestKeyFor } from '../../api/requestKey.ts';
import { collectPages } from '../../api/v2/paging.ts';
import type { components } from '../../api/v2/schema';

// keepPreviousData: a refetch holds the previous render instead of flashing a loader.

type Schemas = components['schemas'];

/** A number the server may send as a number or in its string form. */
const toNumber = (value: number | string): number => (typeof value === 'number' ? value : Number(value));
const toNullableNumber = (value: null | number | string): number | null => (value === null ? null : toNumber(value));

export type StatsGroupBy = 'task' | 'room' | 'user';

export interface StatsPeriod {
  unit: 'weeks' | 'cycles';
  count: number;
}

/** The query of a report: `weeks=n` or `cycles=n`. */
const periodQuery = ({ unit, count }: StatsPeriod): { weeks: string } | { cycles: string } =>
  unit === 'weeks' ? { weeks: String(count) } : { cycles: String(count) };

export interface UserWorkload {
  userId: string;
  plannedMinutes: number;
  doneMinutes: number;
}

export interface WorkloadWeek {
  weekIndex: number;
  startDate: string;
  users: UserWorkload[];
  unassignedPlannedMinutes: number;
}

export interface WorkloadCycle {
  index: number;
  startDate: string;
  endDate: string;
  users: UserWorkload[];
  unassignedPlannedMinutes: number;
  weeks: WorkloadWeek[];
}

export interface WorkloadReport {
  cycles: WorkloadCycle[];
}

export interface CompletionRow {
  /** Null for the row of one-off tasks, roomless one-off tasks or work nobody was assigned to. */
  key: string | null;
  name: string;
  done: number;
  skipped: number;
  missed: number;
  rate: number | null;
}

export interface CompletionReport {
  groupBy: StatsGroupBy;
  rows: CompletionRow[];
}

export interface IntervalRow {
  taskId: string;
  name: string;
  intervalKey: string;
  periodDays: number;
  completions: number;
  averageDays: number | null;
  deviation: number | null;
}

export interface IntervalReport {
  rows: IntervalRow[];
}

export interface DeviationRow {
  taskId: string;
  name: string;
  completions: number;
  averagePlanningShiftDays: number;
  averageCompletionDelayDays: number;
  early: number;
  onTime: number;
  late: number;
}

export interface DeviationReport {
  rows: DeviationRow[];
}

const toUsers = (users: Schemas['UserWorkload'][]): UserWorkload[] =>
  users.map((user) => ({ userId: user.userId, plannedMinutes: toNumber(user.plannedMinutes), doneMinutes: toNumber(user.doneMinutes) }));

export function useWorkload(period: StatsPeriod) {
  return useQuery({
    queryKey: ['stats', 'workload', period.unit, period.count],
    queryFn: async (): Promise<WorkloadReport> => {
      const { data } = await unwrap(apiV2.GET('/api/v2/stats/workload', { params: { query: periodQuery(period) } }));
      return {
        cycles: data.cycles.map((cycle) => ({
          index: toNumber(cycle.index),
          startDate: cycle.startDate,
          endDate: cycle.endDate,
          users: toUsers(cycle.users),
          unassignedPlannedMinutes: toNumber(cycle.unassignedPlannedMinutes),
          weeks: cycle.weeks.map((week) => ({
            weekIndex: toNumber(week.weekIndex),
            startDate: week.startDate,
            users: toUsers(week.users),
            unassignedPlannedMinutes: toNumber(week.unassignedPlannedMinutes),
          })),
        })),
      };
    },
    placeholderData: keepPreviousData,
  });
}

export function useCompletion(period: StatsPeriod, groupBy: StatsGroupBy) {
  return useQuery({
    queryKey: ['stats', 'completion', period.unit, period.count, groupBy],
    queryFn: async (): Promise<CompletionReport> => {
      const { data } = await unwrap(apiV2.GET('/api/v2/stats/completion', { params: { query: { ...periodQuery(period), groupBy } } }));
      return {
        groupBy: data.groupBy as StatsGroupBy,
        rows: data.rows.map((row) => ({
          key: row.key,
          name: row.name,
          done: toNumber(row.done),
          skipped: toNumber(row.skipped),
          missed: toNumber(row.missed),
          rate: toNullableNumber(row.rate),
        })),
      };
    },
    placeholderData: keepPreviousData,
  });
}

export function useIntervals(period: StatsPeriod) {
  return useQuery({
    queryKey: ['stats', 'intervals', period.unit, period.count],
    queryFn: async (): Promise<IntervalReport> => {
      const { data } = await unwrap(apiV2.GET('/api/v2/stats/intervals', { params: { query: periodQuery(period) } }));
      return {
        rows: data.rows.map((row) => ({
          taskId: row.taskId,
          name: row.name,
          intervalKey: row.intervalKey,
          periodDays: toNumber(row.periodDays),
          completions: toNumber(row.completions),
          averageDays: toNullableNumber(row.averageDays),
          deviation: toNullableNumber(row.deviation),
        })),
      };
    },
    placeholderData: keepPreviousData,
  });
}

export function useDeviations(period: StatsPeriod) {
  return useQuery({
    queryKey: ['stats', 'deviations', period.unit, period.count],
    queryFn: async (): Promise<DeviationReport> => {
      const { data } = await unwrap(apiV2.GET('/api/v2/stats/deviations', { params: { query: periodQuery(period) } }));
      return {
        rows: data.rows.map((row) => ({
          taskId: row.taskId,
          name: row.name,
          completions: toNumber(row.completions),
          averagePlanningShiftDays: toNumber(row.averagePlanningShiftDays),
          averageCompletionDelayDays: toNumber(row.averageCompletionDelayDays),
          early: toNumber(row.early),
          onTime: toNumber(row.onTime),
          late: toNumber(row.late),
        })),
      };
    },
    placeholderData: keepPreviousData,
  });
}

/** What a person has earned and redeemed; the money is in whole cents, absent while a point is worth nothing. */
export interface PersonBalance {
  personId: string;
  points: number;
  earned: number;
  redeemed: number;
  money: { earned: number; redeemed: number; balance: number } | null;
  executions: number;
  bonusPoints: number;
}

export interface PointsBalances {
  from: string | null;
  to: string | null;
  currencyCode: string;
  centsPerPoint: number;
  balances: PersonBalance[];
}

/** One line of the points ledger. `kind` is execution, redemption or a bonus kind (ADR-0011, ADR-0012). */
export interface PointEntry {
  id: string;
  key: string;
  kind: string;
  personId: string;
  amount: number;
  date: string;
  weekStart: string;
  periodStart: string | null;
  occurrenceId: string | null;
  taskId: string | null;
  titleSnapshot: string;
  note: string | null;
  /** The cents per point and the currency at the moment of booking; set on a redemption. */
  centsPerPointSnapshot: number | null;
  currencyCodeSnapshot: string | null;
  source: string;
  createdAt: string;
  updatedAt: string;
}

function toBalances(data: Schemas['PointsBalancesResponse']): PointsBalances {
  return {
    from: data.from,
    to: data.to,
    currencyCode: data.currencyCode,
    centsPerPoint: toNumber(data.centsPerPoint),
    balances: data.balances.map((row) => ({
      personId: row.personId,
      points: toNumber(row.points),
      earned: toNumber(row.earned),
      redeemed: toNumber(row.redeemed),
      money: row.money
        ? { earned: toNumber(row.money.earned), redeemed: toNumber(row.money.redeemed), balance: toNumber(row.money.balance) }
        : null,
      executions: toNumber(row.executions),
      bonusPoints: toNumber(row.bonusPoints),
    })),
  };
}

export function toPointEntry(raw: Schemas['PointEntryResponse']): PointEntry {
  return {
    id: raw.id,
    key: raw.key,
    kind: raw.kind,
    personId: raw.personId,
    amount: toNumber(raw.amount),
    date: raw.date,
    weekStart: raw.weekStart,
    periodStart: raw.periodStart,
    occurrenceId: raw.occurrenceId,
    taskId: raw.taskId,
    titleSnapshot: raw.titleSnapshot,
    note: raw.note,
    centsPerPointSnapshot: toNullableNumber(raw.centsPerPointSnapshot),
    currencyCodeSnapshot: raw.currencyCodeSnapshot,
    source: raw.source,
    createdAt: raw.createdAt,
    updatedAt: raw.updatedAt,
  };
}

export function usePointsBalances(range: { from: string; to: string } | null) {
  return useQuery({
    queryKey: ['points', 'balances', range?.from, range?.to],
    queryFn: async () =>
      toBalances((await unwrap(apiV2.GET('/api/v2/points/balances', { params: { query: { from: range!.from, to: range!.to } } }))).data),
    enabled: range !== null,
    placeholderData: keepPreviousData,
  });
}

/** Balances over the whole ledger, without a range: what a person can redeem right now (requirements 4.12). */
export function useAllTimeBalances(enabled = true) {
  return useQuery({
    queryKey: ['points', 'balances', 'all'],
    queryFn: async () => toBalances((await unwrap(apiV2.GET('/api/v2/points/balances'))).data),
    enabled,
  });
}

/** The most the server returns in one page of ledger entries (`limit` 1 to 500). */
const ENTRIES_PAGE_SIZE = 500;

export function usePointsEntries(personId: string | null, range: { from: string; to: string } | null) {
  return useQuery({
    queryKey: ['points', 'entries', personId, range?.from, range?.to],
    queryFn: async (): Promise<PointEntry[]> => {
      const items = await collectPages(async (cursor) =>
        (
          await unwrap(
            apiV2.GET('/api/v2/points/entries', {
              params: { query: { personId: personId!, from: range!.from, to: range!.to, limit: String(ENTRIES_PAGE_SIZE), cursor } },
            }),
          )
        ).data,
      );
      return items.map(toPointEntry);
    },
    enabled: personId !== null && range !== null,
    placeholderData: keepPreviousData,
  });
}

/** What a reset or a purge removed or reset, as the server counted it. */
export interface StatisticsResetResult {
  deletedOccurrences: number;
  deletedRecorded: number;
  resetOccurrences: number;
  resetTasks: number;
  deletedPastCycles: number;
  removedPointEntries: number;
  removedRedemptions: number;
}

/**
 * Starts the statistics over, or purges the history before a day (administrators). A bulk action with no single entity to version, so
 * it carries no `If-Match`.
 */
export function useResetStatistics() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (before?: string): Promise<StatisticsResetResult> => {
      const { data } = await unwrap(apiV2.DELETE('/api/v2/stats', { params: { query: before ? { before } : {} } }));
      return {
        deletedOccurrences: toInt(data.deletedOccurrences),
        deletedRecorded: toInt(data.deletedRecorded),
        resetOccurrences: toInt(data.resetOccurrences),
        resetTasks: toInt(data.resetTasks),
        deletedPastCycles: toInt(data.deletedPastCycles),
        removedPointEntries: toInt(data.removedPointEntries),
        removedRedemptions: toInt(data.removedRedemptions),
      };
    },
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: ['stats'] }),
        queryClient.invalidateQueries({ queryKey: ['points'] }),
        queryClient.invalidateQueries({ queryKey: ['occurrences'] }),
        queryClient.invalidateQueries({ queryKey: ['due'] }),
        queryClient.invalidateQueries({ queryKey: ['tasks'] }),
      ]),
  });
}

export interface RedeemInput {
  personId: string;
  points: number;
  note: string;
}

/**
 * Books a redemption (requirements 4.12). The request key belongs to the intent (person, points and note): a repeated
 * click, a retry after a failure, or a closed and reopened dialog with the same values reuses it, so the
 * server books it once; it is dropped once the request succeeded. Not queued offline: the server decides
 * whether the balance is enough. The body leaves an empty note out: the API refuses an explicit null.
 */
export function useRedeemPoints() {
  const queryClient = useQueryClient();
  return useMutation({
    networkMode: 'always',
    mutationFn: async (input: RedeemInput): Promise<{ entry: PointEntry; replayed: boolean }> => {
      const intent = `redeem:${JSON.stringify(input)}`;
      const requestId = requestKeyFor(intent);
      const body = {
        personId: input.personId,
        points: input.points,
        ...(input.note ? { note: input.note } : {}),
        requestId,
      } as unknown as Schemas['CreateRedemptionRequest'];
      const { data, status } = await unwrap(apiV2.POST('/api/v2/points/redemptions', { body }));
      releaseRequestKey(intent);
      // 201 is a new booking; 200 means the server already had this request and replayed it.
      return { entry: toPointEntry(data), replayed: status === 200 };
    },
    // The balance may also have changed under us (insufficient_balance), so refetch whatever the outcome.
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['points'] }),
  });
}

/** Takes a redemption back: the owner on the day it was booked, an administrator at any time. An intent endpoint: no `If-Match`. */
export function useUndoRedemption() {
  const queryClient = useQueryClient();
  return useMutation({
    networkMode: 'always',
    mutationFn: async (id: string) => (await unwrap(apiV2.DELETE('/api/v2/points/redemptions/{id}', { params: { path: { id } } }))).data,
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['points'] }),
  });
}
