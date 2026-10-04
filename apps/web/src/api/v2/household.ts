import { useQuery } from '@tanstack/react-query';
import { toInt } from '../occurrence.ts';
import { apiV2 } from '../index.ts';
import type { components } from './schema';
import { unwrap } from './client.ts';
import { collectPages } from './paging.ts';

/** The people and the settings of the household as `/api/v2` answers them (`GET /users`, `GET /settings`). */

export type UserRole = 'admin' | 'planner' | 'member';

/** A person as `GET /api/v2/users` answers it. */
export interface User {
  id: string;
  name: string;
  color: string;
  active: boolean;
  role: UserRole;
  /** 0 = Sunday .. 6 = Saturday. */
  unavailableWeekdays: number[];
  dailyBudgetMinutes: { weekday: number; weekend: number };
  maxDailyMinutes: { weekday: number; weekend: number };
  browserNotifications: { enabled: boolean; times: string[] };
  createdAt: string;
  updatedAt: string;
  /** The version of the document: the ETag of a write on it is `"<version>"` (ADR-0022). */
  version: number;
}

type UserResponse = components['schemas']['UserResponse'];

const toRole = (role: string): UserRole => (role === 'admin' || role === 'planner' ? role : 'member');

/** A person as the server answers it (a list item or the answer of a write), the numbers as numbers. */
export const toUser = (user: UserResponse): User => ({
  id: user.id,
  name: user.name,
  color: user.color,
  active: user.active,
  role: toRole(user.role),
  unavailableWeekdays: user.unavailableWeekdays.map(toInt),
  dailyBudgetMinutes: { weekday: toInt(user.dailyBudgetMinutes.weekday), weekend: toInt(user.dailyBudgetMinutes.weekend) },
  maxDailyMinutes: { weekday: toInt(user.maxDailyMinutes.weekday), weekend: toInt(user.maxDailyMinutes.weekend) },
  browserNotifications: { enabled: user.browserNotifications.enabled, times: [...user.browserNotifications.times] },
  createdAt: user.createdAt,
  updatedAt: user.updatedAt,
  version: toInt(user.version),
});

/** The most the server returns in one page of people (`limit` 1 to 500). */
const USER_PAGE_SIZE = 500;

// The key starts with the one of the Node client's list, so an invalidation of `['users']` refreshes this one as well.
export const usersKey = ['users', 'v2'] as const;

export async function fetchUsers(): Promise<User[]> {
  const users = await collectPages(async (cursor) =>
    (await unwrap(apiV2.GET('/api/v2/users', { params: { query: { limit: String(USER_PAGE_SIZE), cursor } } }))).data,
  );
  return users.map(toUser);
}

/** All people, inactive ones included (history still names them). Shares its cache with the profile context. */
export function useUsers() {
  return useQuery({ queryKey: usersKey, queryFn: fetchUsers });
}

/** A task interval: how often it is planned in a 28-day cycle (`perCycle`, null when it is not planned on the grid) and its length. */
export interface Interval {
  key: string;
  label: string;
  perCycle: number | null;
  periodDays: number;
}

export type AiProviderType = 'none' | 'mock' | 'anthropic' | 'openai-compatible' | 'ollama';

export interface AiProvider {
  type: AiProviderType;
  endpoint: string | null;
  model: string | null;
  timeoutSeconds: number | null;
}

export interface AiPromptTemplate {
  system: string;
  user: string;
}

export interface AiPromptTemplates {
  planProposal: AiPromptTemplate;
  planRebalance: AiPromptTemplate;
  taskSuggestions: AiPromptTemplate;
  planExplanation: AiPromptTemplate;
}

export interface BonusAmounts {
  weekDone: number;
  weekOnTime: number;
  cycleDone: number;
  cycleOnTime: number;
}

/** A row of the bonus schedule; `startsInFuture` is the server's word for a row that starts after today. */
export interface BonusScheduleRow extends BonusAmounts {
  from: string;
  startsInFuture: boolean;
}

export type CompletionControl = 'circle' | 'thumb';

/** The household settings as `GET /api/v2/settings` answers them. */
export interface Settings {
  id: string;
  cycleAnchorDate: string;
  weekStartsOn: number;
  timezone: string;
  vacationRanges: { from: string; to: string }[];
  intervals: Interval[];
  aiProvider: AiProvider;
  aiPromptTemplates: AiPromptTemplates | null;
  completionControl: CompletionControl | null;
  promoteThreshold: number;
  bonusSchedule: BonusScheduleRow[];
  /** The amounts in force today, as the server works them out. */
  bonusesInForce: BonusAmounts;
  currencyCode: string;
  centsPerPoint: number;
  rewardGoals: { weekPoints: number | null; cyclePoints: number | null };
  createdAt: string;
  updatedAt: string;
  /** The version of the document: the ETag of a write on it is `"<version>"` (ADR-0022). */
  version: number;
}

type SettingsResponse = components['schemas']['SettingsResponse'];

const toAmounts = (amounts: components['schemas']['BonusAmountsDto']): BonusAmounts => ({
  weekDone: toInt(amounts.weekDone),
  weekOnTime: toInt(amounts.weekOnTime),
  cycleDone: toInt(amounts.cycleDone),
  cycleOnTime: toInt(amounts.cycleOnTime),
});

const toNullableInt = (value: number | string | null): number | null => (value === null ? null : toInt(value));

/** The settings as the server answers them (a read or the answer of a write), the numbers as numbers. */
export const toSettings = (settings: SettingsResponse): Settings => ({
  id: settings.id,
  cycleAnchorDate: settings.cycleAnchorDate,
  weekStartsOn: toInt(settings.weekStartsOn),
  timezone: settings.timezone,
  vacationRanges: settings.vacationRanges.map(({ from, to }) => ({ from, to })),
  intervals: settings.intervals.map((interval) => ({
    key: interval.key,
    label: interval.label,
    perCycle: toNullableInt(interval.perCycle),
    periodDays: toInt(interval.periodDays),
  })),
  aiProvider: {
    type: settings.aiProvider.type as AiProviderType,
    endpoint: settings.aiProvider.endpoint,
    model: settings.aiProvider.model,
    timeoutSeconds: toNullableInt(settings.aiProvider.timeoutSeconds),
  },
  aiPromptTemplates: settings.aiPromptTemplates,
  completionControl: settings.completionControl === 'thumb' ? 'thumb' : settings.completionControl === 'circle' ? 'circle' : null,
  promoteThreshold: toInt(settings.promoteThreshold),
  bonusSchedule: settings.bonusSchedule.map((row) => ({ ...toAmounts(row), from: row.from, startsInFuture: row.startsInFuture })),
  bonusesInForce: toAmounts(settings.bonusesInForce),
  currencyCode: settings.currencyCode,
  centsPerPoint: toInt(settings.centsPerPoint),
  rewardGoals: { weekPoints: toNullableInt(settings.rewardGoals.weekPoints), cyclePoints: toNullableInt(settings.rewardGoals.cyclePoints) },
  createdAt: settings.createdAt,
  updatedAt: settings.updatedAt,
  version: toInt(settings.version),
});

// The key starts with the one of the Node client's read, so an invalidation of `['settings']` refreshes this one as well.
export const settingsKey = ['settings', 'v2'] as const;

export async function fetchSettings(): Promise<Settings> {
  return toSettings((await unwrap(apiV2.GET('/api/v2/settings'))).data);
}

/** The household settings. */
export function useSettings() {
  return useQuery({ queryKey: settingsKey, queryFn: fetchSettings });
}
