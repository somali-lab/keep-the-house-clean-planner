import { defaultPointsForDuration, MAX_TASK_POINTS, MIN_TASK_POINTS } from '@huishoudplanner/shared/points';
import type { MessageKey } from '../../i18n/nl.ts';
import type { RecordWorkInput } from './api.ts';

export type RecordWorkKind = 'extra' | 'oneOff';

/** `done`: recorded as done today by the chosen person; `plan`: an open occurrence on a chosen day. */
export type RecordWorkMode = 'done' | 'plan';

/** Raw form values; numbers stay text until validated. */
export interface RecordWorkForm {
  kind: RecordWorkKind;
  mode: RecordWorkMode;
  taskId: string;
  name: string;
  roomId: string;
  duration: string;
  /** Points of a one-off task as typed; null until edited by hand, so the default for the duration shows. */
  points: string | null;
  /** Who did it (mode `done`). */
  doneBy: string;
  /** The day to plan on (mode `plan`). */
  date: string;
  /** Who it is planned for (mode `plan`); empty means anyone. */
  planFor: string;
}

export type RecordWorkField = 'taskId' | 'name' | 'duration' | 'points' | 'date' | 'doneBy';

/** What is being recorded; the idempotency key belongs to this intent, not to the form. */
export type RecordWorkBody = RecordWorkInput;

export const RECORD_WORK_ERRORS: Record<RecordWorkField, MessageKey> = {
  taskId: 'recordWork.error.task',
  name: 'recordWork.error.name',
  duration: 'recordWork.error.duration',
  points: 'recordWork.error.points',
  date: 'recordWork.error.date',
  doneBy: 'recordWork.error.doneBy',
};

export type RecordWorkResult =
  | { ok: true; body: RecordWorkBody }
  | { ok: false; errors: Partial<Record<RecordWorkField, MessageKey>> };

/** The default points for a duration as form text; '' while the duration is not a whole number of at least 1. */
export function defaultPointsText(duration: string): string {
  const text = duration.trim();
  if (!/^\d+$/.test(text) || Number(text) < 1) return '';
  return String(defaultPointsForDuration(Number(text)));
}

/** What the points field shows: the typed value, or the default for the duration until it is edited by hand. */
export function pointsFieldValue(form: Pick<RecordWorkForm, 'points' | 'duration'>): string {
  return form.points ?? defaultPointsText(form.duration);
}

const DAY_KEY = /^\d{4}-\d{2}-\d{2}$/;

/** Validates the form like the server does and builds the request body without its idempotency key. */
export function buildRecordWork(form: RecordWorkForm, todayKey: string): RecordWorkResult {
  const errors: Partial<Record<RecordWorkField, MessageKey>> = {};
  const planning = form.mode === 'plan';
  if (!planning && !form.doneBy) errors.doneBy = RECORD_WORK_ERRORS.doneBy;
  if (planning) {
    if (!DAY_KEY.test(form.date)) errors.date = RECORD_WORK_ERRORS.date;
    else if (form.date < todayKey) errors.date = 'recordWork.error.dateBefore';
  }
  if (form.kind === 'extra') {
    if (!form.taskId) errors.taskId = RECORD_WORK_ERRORS.taskId;
  } else {
    if (form.name.trim().length === 0) errors.name = RECORD_WORK_ERRORS.name;
    if (!/^\d+$/.test(form.duration.trim()) || Number(form.duration) < 1) errors.duration = RECORD_WORK_ERRORS.duration;
    const points = form.points?.trim() ?? '';
    if (points && (!/^\d+$/.test(points) || Number(points) < MIN_TASK_POINTS || Number(points) > MAX_TASK_POINTS)) {
      errors.points = RECORD_WORK_ERRORS.points;
    }
  }
  if (Object.keys(errors).length > 0) return { ok: false, errors };
  const date = planning ? form.date : todayKey;
  const assigneeId = planning ? form.planFor || null : form.doneBy;
  const done = !planning;
  if (form.kind === 'extra') {
    return { ok: true, body: { kind: 'extra', taskId: form.taskId, date, assigneeId, done } };
  }
  // An empty or untouched field leaves the default to the server, which applies the same rule.
  const points = form.points?.trim() ?? '';
  return {
    ok: true,
    body: {
      kind: 'oneOff',
      name: form.name.trim(),
      roomId: form.roomId || null,
      durationMinutes: Number(form.duration),
      date,
      assigneeId,
      done,
      ...(points ? { points: Number(points) } : {}),
    },
  };
}
