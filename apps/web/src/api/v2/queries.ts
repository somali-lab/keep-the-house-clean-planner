import { useQuery } from '@tanstack/react-query';
import { toInt } from '../occurrence.ts';
import { apiV2 } from '../index.ts';
import type { components } from './schema';
import { unwrap } from './client.ts';
import { collectPages } from './paging.ts';

/** The limits and defaults the web app needs from the server (`GET /api/v2/meta/limits`). */
export interface Limits {
  calendar: { maxRangeDays: number };
  tasks: {
    minPoints: number;
    maxPoints: number;
    minDurationMinutes: number;
    oneOffNameMaxLength: number;
    skipReasonMaxLength: number;
  };
  /** The money conversion: cents one point is worth. */
  points: { minCentsPerPoint: number; maxCentsPerPoint: number };
  /** The amount of one period bonus. */
  bonuses: { minPoints: number; maxPoints: number };
  /** The goal of the reward meter, in points. */
  rewards: { minGoalPoints: number; maxGoalPoints: number };
  notifications: { maxBrowserTimes: number };
  ai: { minTimeoutSeconds: number; maxTimeoutSeconds: number; defaultTimeoutSeconds: number };
  defaults: { currencyCode: string };
}

/** What the forms assume until the limits are read (the values the server reports today). The server validates every write too. */
export const FALLBACK_LIMITS = {
  points: { minCentsPerPoint: 0, maxCentsPerPoint: 10_000 },
  bonuses: { minPoints: 0, maxPoints: 1000 },
  rewards: { minGoalPoints: 0, maxGoalPoints: 100_000 },
  notifications: { maxBrowserTimes: 6 },
  ai: { minTimeoutSeconds: 10, maxTimeoutSeconds: 900, defaultTimeoutSeconds: 180 },
  defaults: { currencyCode: 'EUR' },
} as const satisfies Pick<Limits, 'points' | 'bonuses' | 'rewards' | 'notifications' | 'ai' | 'defaults'>;

export const limitsKey = ['limits'] as const;

/** Read once per session: the values never change while the server runs. */
export function useLimits() {
  return useQuery({
    queryKey: limitsKey,
    staleTime: Infinity,
    gcTime: Infinity,
    queryFn: async (): Promise<Limits> => {
      const { data } = await unwrap(apiV2.GET('/api/v2/meta/limits'));
      const tasks = data.tasks;
      return {
        calendar: { maxRangeDays: toInt(data.calendar?.maxRangeDays ?? 371) },
        tasks: {
          minPoints: toInt(tasks?.minPoints ?? 0),
          maxPoints: toInt(tasks?.maxPoints ?? 1000),
          minDurationMinutes: toInt(tasks?.minDurationMinutes ?? 1),
          oneOffNameMaxLength: toInt(tasks?.oneOffNameMaxLength ?? 120),
          skipReasonMaxLength: toInt(tasks?.skipReasonMaxLength ?? 500),
        },
        points: {
          minCentsPerPoint: toInt(data.points?.minCentsPerPoint ?? FALLBACK_LIMITS.points.minCentsPerPoint),
          maxCentsPerPoint: toInt(data.points?.maxCentsPerPoint ?? FALLBACK_LIMITS.points.maxCentsPerPoint),
        },
        bonuses: {
          minPoints: toInt(data.bonuses?.minPoints ?? FALLBACK_LIMITS.bonuses.minPoints),
          maxPoints: toInt(data.bonuses?.maxPoints ?? FALLBACK_LIMITS.bonuses.maxPoints),
        },
        rewards: {
          minGoalPoints: toInt(data.rewards?.minGoalPoints ?? FALLBACK_LIMITS.rewards.minGoalPoints),
          maxGoalPoints: toInt(data.rewards?.maxGoalPoints ?? FALLBACK_LIMITS.rewards.maxGoalPoints),
        },
        notifications: { maxBrowserTimes: toInt(data.notifications?.maxBrowserTimes ?? FALLBACK_LIMITS.notifications.maxBrowserTimes) },
        ai: {
          minTimeoutSeconds: toInt(data.ai?.minTimeoutSeconds ?? FALLBACK_LIMITS.ai.minTimeoutSeconds),
          maxTimeoutSeconds: toInt(data.ai?.maxTimeoutSeconds ?? FALLBACK_LIMITS.ai.maxTimeoutSeconds),
          defaultTimeoutSeconds: toInt(data.defaults?.aiTimeoutSeconds ?? FALLBACK_LIMITS.ai.defaultTimeoutSeconds),
        },
        defaults: { currencyCode: data.defaults?.currencyCode ?? FALLBACK_LIMITS.defaults.currencyCode },
      };
    },
  });
}

/** Where a day falls in the cycles, as the server counts it. */
export interface CalendarDay {
  dayKey: string;
  /** 0 = Sunday .. 6 = Saturday. */
  weekday: number;
  /** Negative before the cycle anchor. */
  cycleIndex: number;
  /** The week of the cycle, 0 to 3. */
  weekIndex: number;
  isoWeek: string;
  weekStart: string;
}

export const calendarKey = (from: string, to: string) => ['calendar', from, to] as const;

/** Cycle index, week index and ISO week of every day from `from` to `to` (both included). */
export function useCalendar(from: string, to: string, enabled = true) {
  return useQuery({
    queryKey: calendarKey(from, to),
    enabled,
    queryFn: async (): Promise<Map<string, CalendarDay>> => {
      const { data } = await unwrap(apiV2.GET('/api/v2/calendar', { params: { query: { from, to } } }));
      return new Map(
        data.days.map((day) => [
          day.dayKey,
          {
            dayKey: day.dayKey,
            weekday: toInt(day.weekday),
            cycleIndex: toInt(day.cycleIndex),
            weekIndex: toInt(day.weekIndex),
            isoWeek: day.isoWeek,
            weekStart: day.weekStart,
          },
        ]),
      );
    },
  });
}

/** A room as `GET /api/v2/rooms` answers it. */
export interface Room {
  id: string;
  name: string;
  sortOrder: number;
  active: boolean;
  virtual: boolean;
  createdAt: string;
  updatedAt: string;
  /** The version of the document: the ETag of a write on it is `"<version>"` (ADR-0022). */
  version: number;
}

/** A task as `GET /api/v2/tasks` answers it; the points are always the value in force. */
export interface Task {
  id: string;
  name: string;
  roomId: string;
  intervalKey: string;
  durationMinutes: number;
  points: number;
  defaultAssigneeId: string | null;
  active: boolean;
  notes: string;
  tags: string[];
  lastCompletedAt: string | null;
  createdAt: string;
  updatedAt: string;
  /** The version of the document: the ETag of a write on it is `"<version>"` (ADR-0022). */
  version: number;
}

/** The most the server returns in one page of rooms or tasks (`limit` 1 to 200). */
const LIST_PAGE_SIZE = 200;

// The keys start with the ones the Node-client queries use, so an invalidation of `['rooms']` or `['tasks']`
// (settings, planner, history, the task and room mutations) refreshes these as well.
export const roomsKey = ['rooms', 'v2'] as const;
export const tasksKey = ['tasks', 'v2'] as const;

type RoomResponse = components['schemas']['RoomResponse'];
type TaskResponse = components['schemas']['TaskResponse'];

/** A room as the server answers it, the numbers as numbers. */
export const toRoom = (room: RoomResponse): Room => ({ ...room, sortOrder: toInt(room.sortOrder), version: toInt(room.version) });

/** A task as the server answers it (a list item or the answer of a write), the numbers as numbers. */
export const toTask = (task: TaskResponse): Task => ({
  ...task,
  durationMinutes: toInt(task.durationMinutes),
  points: toInt(task.points),
  version: toInt(task.version),
});

export async function fetchRooms(): Promise<Room[]> {
  const rooms = await collectPages(async (cursor) =>
    (await unwrap(apiV2.GET('/api/v2/rooms', { params: { query: { limit: String(LIST_PAGE_SIZE), cursor } } }))).data,
  );
  return rooms.map(toRoom);
}

export async function fetchTasks(): Promise<Task[]> {
  const tasks = await collectPages(async (cursor) =>
    (await unwrap(apiV2.GET('/api/v2/tasks', { params: { query: { limit: String(LIST_PAGE_SIZE), cursor } } }))).data,
  );
  return tasks.map(toTask);
}

/** All rooms, inactive ones included. */
export function useRooms() {
  return useQuery({ queryKey: roomsKey, queryFn: fetchRooms });
}

/** All tasks, inactive ones included. */
export function useTasks() {
  return useQuery({ queryKey: tasksKey, queryFn: fetchTasks });
}
