import type { CreateTaskInput, Task } from '@huishoudplanner/shared';
import type { MessageKey } from '../../i18n/nl.ts';

/** Raw form state: everything is a string, as in the inputs. */
export interface TaskFormValues {
  name: string;
  roomId: string;
  intervalKey: string;
  durationMinutes: string;
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
  return errors;
}

export function toTaskInput(values: TaskFormValues): CreateTaskInput {
  return {
    name: values.name.trim(),
    roomId: values.roomId,
    intervalKey: values.intervalKey,
    durationMinutes: Number(values.durationMinutes.trim()),
    defaultAssigneeId: values.defaultAssigneeId || null,
    notes: values.notes,
    tags: values.tags
      .split(',')
      .map((tag) => tag.trim())
      .filter(Boolean),
  };
}
