import type { CreateTaskInput, Task } from '@huishoudplanner/shared';
import { defaultPointsForDuration, MAX_TASK_POINTS, MIN_TASK_POINTS } from '@huishoudplanner/shared/points';
import type { MessageKey } from '../../i18n/nl.ts';

/** Raw form state: everything is a string, as in the inputs. */
export interface TaskFormValues {
  name: string;
  roomId: string;
  intervalKey: string;
  durationMinutes: string;
  /** Whole number 0..100; '' lets the server default it from the duration. */
  points: string;
  /** '' = "wie dan ook" */
  defaultAssigneeId: string;
  notes: string;
  /** comma separated */
  tags: string;
}

export type TaskFormErrors = Partial<Record<keyof TaskFormValues, MessageKey>>;

export function emptyTaskForm(defaults: Partial<TaskFormValues> = {}): TaskFormValues {
  return {
    name: '',
    roomId: '',
    intervalKey: '',
    durationMinutes: '',
    points: '',
    defaultAssigneeId: '',
    notes: '',
    tags: '',
    ...defaults,
  };
}

export function taskToForm(task: Task): TaskFormValues {
  return {
    name: task.name,
    roomId: task.roomId,
    intervalKey: task.intervalKey,
    durationMinutes: String(task.durationMinutes),
    points: String(task.points ?? defaultPointsForDuration(task.durationMinutes)),
    defaultAssigneeId: task.defaultAssigneeId ?? '',
    notes: task.notes,
    tags: task.tags.join(', '),
  };
}

/** Same rules as the server: name, room, interval and a whole number of minutes ≥ 1 are required. */
export function validateTaskForm(values: TaskFormValues): TaskFormErrors {
  const errors: TaskFormErrors = {};
  if (!values.name.trim()) errors.name = 'tasks.error.nameRequired';
  if (!values.roomId) errors.roomId = 'tasks.error.roomRequired';
  if (!values.intervalKey) errors.intervalKey = 'tasks.error.intervalRequired';
  const duration = values.durationMinutes.trim();
  if (!duration) errors.durationMinutes = 'tasks.error.durationRequired';
  else if (!/^\d+$/.test(duration) || Number(duration) < 1) errors.durationMinutes = 'tasks.error.durationInvalid';
  const points = values.points.trim();
  if (points && (!/^\d+$/.test(points) || Number(points) < MIN_TASK_POINTS || Number(points) > MAX_TASK_POINTS)) {
    errors.points = 'tasks.error.pointsInvalid';
  }
  return errors;
}

/** The default points for a duration as form text; '' while the duration is not a whole number of at least 1. */
export function defaultPointsText(duration: string): string {
  const text = duration.trim();
  if (!/^\d+$/.test(text) || Number(text) < 1) return '';
  return String(defaultPointsForDuration(Number(text)));
}

/**
 * Whether the points were set by hand: they are filled in from the duration until the person
 * types a value of their own (ADR-0011). A value that equals the default still follows the duration.
 */
export function pointsEditedByHand(values: TaskFormValues): boolean {
  return values.points.trim() !== '' && values.points.trim() !== defaultPointsText(values.durationMinutes);
}

export function toTaskInput(values: TaskFormValues): CreateTaskInput {
  return {
    name: values.name.trim(),
    roomId: values.roomId,
    intervalKey: values.intervalKey,
    durationMinutes: Number(values.durationMinutes.trim()),
    ...(values.points.trim() === '' ? {} : { points: Number(values.points.trim()) }),
    defaultAssigneeId: values.defaultAssigneeId || null,
    notes: values.notes,
    tags: values.tags
      .split(',')
      .map((tag) => tag.trim())
      .filter(Boolean),
  };
}
