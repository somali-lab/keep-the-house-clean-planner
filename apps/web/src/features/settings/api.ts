import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiV2, ApiRequestError, ifMatch, isStaleEntity, removeFromList, replaceInList, unwrap } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';
import {
  settingsKey,
  toSettings,
  toUser,
  usersKey,
  type AiPromptTemplates,
  type AiProviderType,
  type BonusAmounts,
  type CompletionControl,
  type Settings,
  type User,
} from '../../api/v2/household.ts';
import { collectPages } from '../../api/v2/paging.ts';
import { roomsKey, toRoom, type Room } from '../../api/v2/queries.ts';
import type { components } from '../../api/v2/schema';
import { format, t, type MessageKey } from '../../i18n/nl.ts';

type Schemas = components['schemas'];

/**
 * What a settings save may carry. The API reads an explicit `null` as a mistake for every optional member (a 400), so a member is
 * either left out or has a value; `rewardGoals` is the one that holds nulls on purpose (an automatic goal).
 */
export interface SettingsPatch {
  cycleAnchorDate?: string;
  vacationRanges?: { from: string; to: string }[];
  aiProvider?: { type: AiProviderType; endpoint?: string; model?: string; timeoutSeconds?: number };
  aiPromptTemplates?: AiPromptTemplates;
  completionControl?: CompletionControl;
  periodBonuses?: BonusAmounts;
  currencyCode?: string;
  centsPerPoint?: number;
  rewardGoals?: { weekPoints: number | null; cyclePoints: number | null };
}

/**
 * Saves part of the settings (administrators). The write carries the version of the settings the person is editing as `If-Match`
 * (ADR-0022); the answer goes straight into the cache, so a second save needs no re-read. A stale version (412) re-reads the
 * settings and leaves the person's edit in its form; the caller shows `saveErrorText`.
 */
export function useUpdateSettings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ settings, patch }: { settings: Settings; patch: SettingsPatch }): Promise<Settings> =>
      toSettings(
        (
          await unwrap(
            apiV2.PATCH('/api/v2/settings', {
              params: { header: ifMatch(settings) },
              body: patch as unknown as Schemas['UpdateSettingsRequest'],
            }),
          )
        ).data,
      ),
    onSuccess: (saved) => {
      queryClient.setQueryData(settingsKey, saved);
    },
    onError: async (error) => {
      // A stale version (412) and a bonus row that someone else wrote first (409) both mean the settings moved on: read them again,
      // so that the message is true and the next save carries the stored version.
      if (isStaleEntity(error) || (error instanceof ApiRequestError && error.code === 'bonus_schedule_conflict')) {
        await queryClient.invalidateQueries({ queryKey: settingsKey });
      }
    },
  });
}

/** The text of a failed save: the stale message for a changed entity, otherwise the one the caller picks for that code, or the generic one. */
export function saveErrorText(error: unknown, byCode: Partial<Record<string, MessageKey>> = {}): string {
  if (isStaleEntity(error)) return t('app.staleEntity');
  if (error instanceof ApiRequestError) {
    const key = byCode[error.code];
    if (key) return t(key);
    const first = firstFieldError(error);
    if (first) return format('app.validationField', first);
  }
  return t('app.error');
}

/** The first refused field of a `400 validation_error` with its reason, from the `errors` of the Problem Details. */
function firstFieldError(error: ApiRequestError): { field: string; reason: string } | null {
  if (error.code !== 'validation_error' || typeof error.details !== 'object' || error.details === null || Array.isArray(error.details)) return null;
  for (const [field, reasons] of Object.entries(error.details)) {
    const reason = Array.isArray(reasons) ? reasons.find((item) => typeof item === 'string') : typeof reasons === 'string' ? reasons : undefined;
    if (typeof reason === 'string') return { field, reason };
  }
  return null;
}

/** A person, as the form builds it. */
export interface UserInput {
  name: string;
  color: string;
  role: User['role'];
  unavailableWeekdays: number[];
  dailyBudgetMinutes: { weekday: number; weekend: number };
  maxDailyMinutes: { weekday: number; weekend: number };
}

/** Creates a person, or changes one (with `If-Match` of the version the person was read at). Administrators only. */
export function useSaveUser() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ user, input, active }: { user?: User; input: UserInput; active: boolean }): Promise<User> => {
      if (user) {
        const body = { ...input, active } as unknown as Schemas['UpdateUserRequest'];
        return toUser(
          (await unwrap(apiV2.PATCH('/api/v2/users/{id}', { params: { path: { id: user.id }, header: ifMatch(user) }, body }))).data,
        );
      }
      return toUser((await unwrap(apiV2.POST('/api/v2/users', { body: input as unknown as Schemas['CreateUserRequest'] }))).data);
    },
    onSuccess: async (saved, { user }) => {
      if (user) replaceInList(queryClient, usersKey, saved);
      await queryClient.invalidateQueries({ queryKey: usersKey });
    },
    onError: async (error) => {
      if (isStaleEntity(error)) await queryClient.invalidateQueries({ queryKey: usersKey });
    },
  });
}

/** Creates a room, or changes one (with `If-Match` of the version it was read at). Administrators only. */
export function useSaveRoom() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ room, name, sortOrder, active }: { room?: Room; name: string; sortOrder?: number; active: boolean }): Promise<Room> => {
      if (room) {
        const body = { name, ...(sortOrder === undefined ? {} : { sortOrder }), active } as unknown as Schemas['UpdateRoomRequest'];
        return toRoom(
          (await unwrap(apiV2.PATCH('/api/v2/rooms/{id}', { params: { path: { id: room.id }, header: ifMatch(room) }, body }))).data,
        );
      }
      const body = { name, ...(sortOrder === undefined ? {} : { sortOrder }) } as unknown as Schemas['CreateRoomRequest'];
      return toRoom((await unwrap(apiV2.POST('/api/v2/rooms', { body }))).data);
    },
    onSuccess: async (saved, { room }) => {
      if (room) replaceInList(queryClient, roomsKey, saved);
      await queryClient.invalidateQueries({ queryKey: ['rooms'] });
    },
    onError: async (error) => {
      if (isStaleEntity(error)) await queryClient.invalidateQueries({ queryKey: ['rooms'] });
    },
  });
}

/** Deletes a room that no task uses (`409 room_in_use` otherwise). */
export function useDeleteRoom() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (room: Room) => {
      await unwrap(apiV2.DELETE('/api/v2/rooms/{id}', { params: { path: { id: room.id }, header: ifMatch(room) } }));
      return room.id;
    },
    onSuccess: async (id) => {
      removeFromList(queryClient, roomsKey, id);
      await queryClient.invalidateQueries({ queryKey: ['rooms'] });
    },
    onError: async (error) => {
      if (isStaleEntity(error)) await queryClient.invalidateQueries({ queryKey: ['rooms'] });
    },
  });
}

/** The number of redemptions that exist, for the import warning about an older file. */
export function useRedemptionCount(enabled: boolean) {
  return useQuery({
    queryKey: ['points', 'redemptions', 'count'],
    queryFn: async () => toInt((await unwrap(apiV2.GET('/api/v2/points/redemptions/count'))).data.count),
    enabled,
    retry: false,
    gcTime: 0,
  });
}

/** The number of badges that exist (at most 100 can), for the import warning about an older file. */
export function useBadgeCount(enabled: boolean) {
  return useQuery({
    queryKey: ['badges', 'count'],
    queryFn: async () =>
      (
        await collectPages(async (cursor) =>
          (await unwrap(apiV2.GET('/api/v2/badges', { params: { query: { limit: '100', cursor } } }))).data,
        )
      ).length,
    enabled,
    retry: false,
    gcTime: 0,
  });
}
