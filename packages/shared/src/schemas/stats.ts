import { z } from 'zod';

export const statsCyclesQuerySchema = z.object({
  cycles: z.coerce.number().int().min(1).max(26).default(4),
});

export const statsGroupBySchema = z.enum(['task', 'room', 'user']);
export type StatsGroupBy = z.infer<typeof statsGroupBySchema>;

export const completionQuerySchema = statsCyclesQuerySchema.extend({ groupBy: statsGroupBySchema });

export interface UserWorkload {
  userId: string;
  /** Snapshot minutes of occurrences assigned to this person. */
  plannedMinutes: number;
  /** Snapshot minutes of occurrences this person completed (completedBy). */
  doneMinutes: number;
}

export interface WorkloadPeriod {
  users: UserWorkload[];
  /** Planned minutes without an assignee ("wie dan ook"). */
  unassignedPlannedMinutes: number;
}

export interface WorkloadWeek extends WorkloadPeriod {
  weekIndex: number;
  startDate: string;
}

export interface WorkloadCycle extends WorkloadPeriod {
  index: number;
  startDate: string;
  endDate: string;
  weeks: WorkloadWeek[];
}

export interface WorkloadResponse {
  /** Oldest first, so the list reads as a trend. */
  cycles: WorkloadCycle[];
}

export interface CompletionRow {
  /** Task, room or user id; null for occurrences without assignee (groupBy=user). */
  key: string | null;
  name: string;
  done: number;
  skipped: number;
  /** Still open on a day before today. */
  missed: number;
  /** done / (done + skipped + missed); null when nothing was due yet. */
  rate: number | null;
}

export interface CompletionResponse {
  groupBy: StatsGroupBy;
  rows: CompletionRow[];
}

export interface IntervalRow {
  taskId: string;
  name: string;
  intervalKey: string;
  periodDays: number;
  completions: number;
  /** Average local calendar days between consecutive completions; null with fewer than two. */
  averageDays: number | null;
  /** averageDays / periodDays; above 1 means it happens less often than intended. */
  deviation: number | null;
}

export interface IntervalsResponse {
  rows: IntervalRow[];
}
