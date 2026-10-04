import type { Task } from '../../api/v2/queries.ts';
import type { MessageKey } from '../../i18n/nl.ts';

/** Raw form state: everything is a string, as in the inputs. */
export interface TaskFormValues {
  name: string;
  roomId: string;
  intervalKey: string;
  durationMinutes: string;
  /** A whole number within the limits of the server; '' on a new task lets the server default it from the duration. */
  points: string;
  /** '' = "wie dan ook" */
  defaultAssigneeId: string;
  notes: string;
  /** comma separated */
  tags: string;
}

export type TaskFormErrors = Partial<Record<keyof TaskFormValues, MessageKey>>;

/** The task limits of `GET /api/v2/meta/limits` that the form checks. */
export interface TaskFormLimits {
  minPoints: number;
  maxPoints: number;
}

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
    points: String(task.points),
    defaultAssigneeId: task.defaultAssigneeId ?? '',
    notes: task.notes,
    tags: task.tags.join(', '),
  };
}

/**
 * Early feedback on the rules of the server: name, room, interval and a whole number of minutes of at least 1 are
 * required, and the points are a whole number within the limits. The server stays the judge: it answers every
 * rule again, also while the limits are not loaded yet.
 */
export function validateTaskForm(
  values: TaskFormValues,
  options: { limits?: TaskFormLimits; mode?: 'create' | 'edit' } = {},
): TaskFormErrors {
  const errors: TaskFormErrors = {};
  if (!values.name.trim()) errors.name = 'tasks.error.nameRequired';
  if (!values.roomId) errors.roomId = 'tasks.error.roomRequired';
  if (!values.intervalKey) errors.intervalKey = 'tasks.error.intervalRequired';
  const duration = values.durationMinutes.trim();
  if (!duration) errors.durationMinutes = 'tasks.error.durationRequired';
  else if (!/^\d+$/.test(duration) || Number(duration) < 1) errors.durationMinutes = 'tasks.error.durationInvalid';
  const points = values.points.trim();
  if (points === '') {
    // Only a new task can leave the points to the server's default; an update cannot ask for it.
    if (options.mode === 'edit') errors.points = 'tasks.error.pointsRequired';
  } else {
    const { limits } = options;
    const outOfRange = limits !== undefined && (Number(points) < limits.minPoints || Number(points) > limits.maxPoints);
    if (!/^\d+$/.test(points) || outOfRange) errors.points = 'tasks.error.pointsInvalid';
  }
  return errors;
}

/** The body of `POST /api/v2/tasks` and `PATCH /api/v2/tasks/{id}`. */
export interface TaskBody {
  name: string;
  roomId: string;
  intervalKey: string;
  durationMinutes: number;
  points?: number;
  /** An explicit null means "anyone"; the API documents it. */
  defaultAssigneeId: string | null;
  notes: string;
  tags: string[];
}

/**
 * The request body. An empty points field is left out of the body: a new task then gets the default for its
 * duration from the server (ADR-0011), and the form does not let an update go out without points. Optional
 * fields are omitted, never sent as null: the server refuses an explicit null for them.
 */
export function toTaskInput(values: TaskFormValues): TaskBody {
  const points = values.points.trim();
  return {
    name: values.name.trim(),
    roomId: values.roomId,
    intervalKey: values.intervalKey,
    durationMinutes: Number(values.durationMinutes.trim()),
    ...(points === '' ? {} : { points: Number(points) }),
    defaultAssigneeId: values.defaultAssigneeId || null,
    notes: values.notes,
    tags: values.tags
      .split(',')
      .map((tag) => tag.trim())
      .filter(Boolean),
  };
}
