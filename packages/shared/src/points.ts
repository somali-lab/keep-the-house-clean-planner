/** Smallest and largest points value of a task (ADR-0011). 0 means the task earns nothing. */
export const MIN_TASK_POINTS = 0;
export const MAX_TASK_POINTS = 1000;

/**
 * Default points of a task or one-off task: one point per minute, between 1 and the maximum
 * (ADR-0011).
 */
export function defaultPointsForDuration(minutes: number): number {
  if (!Number.isFinite(minutes)) return 1;
  return Math.min(MAX_TASK_POINTS, Math.max(1, Math.round(minutes)));
}
