import type { components } from './v2/schema';

type OccurrenceResponse = components['schemas']['OccurrenceResponse'];

/** The server sends whole numbers as numbers; the generated types also allow their string form. */
export const toInt = (value: number | string): number => (typeof value === 'number' ? value : Number(value));

export type OccurrenceStatus = 'open' | 'done' | 'skipped';

/** An occurrence as the web app handles it: `GET /api/v2/occurrences` and the answers of its intent endpoints. */
export interface Occurrence {
  id: string;
  /** Null for a one-off task, whose name, duration and room live in the snapshot fields. */
  taskId: string | null;
  cycleId: string;
  planId: string | null;
  /** The day it sits on now. */
  date: string;
  /** The day of its slot; kept when the occurrence is moved. */
  plannedDate: string;
  assigneeId: string | null;
  status: OccurrenceStatus;
  /** Where an undo goes back to; null while not done. */
  statusBeforeCompletion: 'open' | 'skipped' | null;
  completedAt: string | null;
  completedBy: string | null;
  skipReason: string | null;
  durationMinutesSnapshot: number;
  taskNameSnapshot: string;
  roomIdSnapshot: string | null;
  roomNameSnapshot: string | null;
  origin: 'generated' | 'adhoc';
  /** Created directly in the done state: there is no planned state to return to. */
  recordedDone: boolean;
  pointsSnapshot: number | null;
  pointsOverride: number | null;
  createdAt: string;
  updatedAt: string;
  isOverdue: boolean;
  movedFrom: string | null;
  /** Where the day falls in the cycles, as the server computes it. */
  cycleIndex: number;
  weekIndex: number;
}

export function toOccurrence(raw: OccurrenceResponse): Occurrence {
  return {
    id: raw.id,
    taskId: raw.taskId,
    cycleId: raw.cycleId,
    planId: raw.planId,
    date: raw.date,
    plannedDate: raw.plannedDate,
    assigneeId: raw.assigneeId,
    status: raw.status as OccurrenceStatus,
    statusBeforeCompletion: raw.statusBeforeCompletion as Occurrence['statusBeforeCompletion'],
    completedAt: raw.completedAt,
    completedBy: raw.completedBy,
    skipReason: raw.skipReason,
    durationMinutesSnapshot: toInt(raw.durationMinutesSnapshot),
    taskNameSnapshot: raw.taskNameSnapshot,
    roomIdSnapshot: raw.roomIdSnapshot,
    roomNameSnapshot: raw.roomNameSnapshot,
    origin: raw.origin as Occurrence['origin'],
    recordedDone: raw.recordedDone,
    pointsSnapshot: raw.pointsSnapshot === null ? null : toInt(raw.pointsSnapshot),
    pointsOverride: raw.pointsOverride === null ? null : toInt(raw.pointsOverride),
    createdAt: raw.createdAt,
    updatedAt: raw.updatedAt,
    isOverdue: raw.isOverdue,
    movedFrom: raw.movedFrom,
    cycleIndex: toInt(raw.cycleIndex),
    weekIndex: toInt(raw.weekIndex),
  };
}
