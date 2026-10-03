/** Smallest and largest points value of a task (ADR-0011). 0 means the task earns nothing. */
export const MIN_TASK_POINTS = 0;
export const MAX_TASK_POINTS = 100;

/** Minutes of work that earn one point by default. */
export const MINUTES_PER_POINT = 10;

/**
 * Default points of a task or one-off task: one point for every ten minutes, or part of ten
 * minutes, between 1 and 100 (ADR-0011).
 */
export function defaultPointsForDuration(minutes: number): number {
  const raw = Math.ceil(minutes / MINUTES_PER_POINT);
  if (!Number.isFinite(raw)) return 1;
  return Math.min(MAX_TASK_POINTS, Math.max(1, raw));
}
