import { QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { LIMITS, mockApi, testQueryClient } from '../../test/fixtures.ts';
import { useSettings, useUsers } from './household.ts';
import { FALLBACK_LIMITS, useLimits } from './queries.ts';

function wrapperFor(queryClient = testQueryClient()) {
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}

const user = (id: string, extra = {}) => ({
  id,
  name: `Persoon ${id}`,
  color: '#2563eb',
  active: true,
  role: 'planner',
  unavailableWeekdays: [1, '6'],
  dailyBudgetMinutes: { weekday: '60', weekend: 120 },
  maxDailyMinutes: { weekday: 480, weekend: '480' },
  browserNotifications: { enabled: true, times: ['08:00'] },
  createdAt: 'x',
  updatedAt: 'x',
  version: '3',
  ...extra,
});

describe('useUsers', () => {
  it('reads every page of the people with the largest page size and maps the numbers', async () => {
    const fetchMock = mockApi({
      '/api/v2/users': (_init: RequestInit | undefined, url: string) =>
        new URL(url, 'http://localhost').searchParams.get('cursor') === 'c2'
          ? { items: [user('u3', { role: 'unknown' })], nextCursor: null }
          : { items: [user('u1'), user('u2', { active: false })], nextCursor: 'c2' },
    });
    const { result } = renderHook(() => useUsers(), { wrapper: wrapperFor() });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual(['/api/v2/users?limit=500', '/api/v2/users?limit=500&cursor=c2']);
    expect(result.current.data!.map((u) => u.id)).toEqual(['u1', 'u2', 'u3']);
    expect(result.current.data![0]).toMatchObject({
      role: 'planner',
      unavailableWeekdays: [1, 6],
      dailyBudgetMinutes: { weekday: 60, weekend: 120 },
      maxDailyMinutes: { weekday: 480, weekend: 480 },
      version: 3,
    });
    // A role the web app does not know is shown as the least privileged one.
    expect(result.current.data![2]!.role).toBe('member');
  });
});

describe('useSettings', () => {
  const settings = {
    id: 's1',
    cycleAnchorDate: '2026-09-14',
    weekStartsOn: '1',
    timezone: 'Europe/Amsterdam',
    vacationRanges: [{ from: '2026-12-21', to: '2027-01-03' }],
    intervals: [
      { key: 'daily', label: 'Dagelijks', perCycle: '28', periodDays: 1 },
      { key: 'quarter', label: 'Kwartaal', perCycle: null, periodDays: '91' },
    ],
    aiProvider: { type: 'ollama', endpoint: 'http://x', model: 'm', timeoutSeconds: '240' },
    aiPrompts: null,
    aiPromptTemplates: null,
    completionControl: 'thumb',
    promoteThreshold: 2,
    dismissedPromotions: [],
    bonusSchedule: [{ from: '2026-09-01', weekDone: '4', weekOnTime: 2, cycleDone: 10, cycleOnTime: 5, startsInFuture: true }],
    bonusesInForce: { weekDone: '4', weekOnTime: 2, cycleDone: 10, cycleOnTime: 5 },
    bonusFloor: null,
    currencyCode: 'EUR',
    centsPerPoint: '25',
    rewardGoals: { weekPoints: '12', cyclePoints: null },
    createdAt: 'x',
    updatedAt: 'x',
    version: '7',
  };

  it('maps the numbers, keeps what the server worked out and ignores what the web app does not use', async () => {
    mockApi({ '/api/v2/settings': settings });
    const { result } = renderHook(() => useSettings(), { wrapper: wrapperFor() });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toMatchObject({
      weekStartsOn: 1,
      completionControl: 'thumb',
      intervals: [
        { key: 'daily', perCycle: 28, periodDays: 1 },
        { key: 'quarter', perCycle: null, periodDays: 91 },
      ],
      aiProvider: { type: 'ollama', endpoint: 'http://x', model: 'm', timeoutSeconds: 240 },
      bonusSchedule: [{ from: '2026-09-01', weekDone: 4, startsInFuture: true }],
      bonusesInForce: { weekDone: 4, weekOnTime: 2, cycleDone: 10, cycleOnTime: 5 },
      centsPerPoint: 25,
      rewardGoals: { weekPoints: 12, cyclePoints: null },
      version: 7,
    });
    expect(result.current.data).not.toHaveProperty('dismissedPromotions');
  });

  it('has no completion control when the server has none', async () => {
    mockApi({ '/api/v2/settings': { ...settings, completionControl: null } });
    const { result } = renderHook(() => useSettings(), { wrapper: wrapperFor() });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data!.completionControl).toBeNull();
  });
});

describe('useLimits for the settings forms', () => {
  it('reads the limits of points, bonuses, rewards, notifications and the AI provider', async () => {
    mockApi({
      '/api/v2/meta/limits': {
        ...LIMITS,
        points: { minCentsPerPoint: '0', maxCentsPerPoint: '500' },
        bonuses: { minPoints: 1, maxPoints: '50' },
        rewards: { minGoalPoints: 0, maxGoalPoints: 900 },
        notifications: { maxBrowserTimes: 4 },
        ai: { minTimeoutSeconds: 20, maxTimeoutSeconds: 100 },
        defaults: { currencyCode: 'USD', aiTimeoutSeconds: 60 },
      },
    });
    const { result } = renderHook(() => useLimits(), { wrapper: wrapperFor() });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toMatchObject({
      points: { minCentsPerPoint: 0, maxCentsPerPoint: 500 },
      bonuses: { minPoints: 1, maxPoints: 50 },
      rewards: { minGoalPoints: 0, maxGoalPoints: 900 },
      notifications: { maxBrowserTimes: 4 },
      ai: { minTimeoutSeconds: 20, maxTimeoutSeconds: 100, defaultTimeoutSeconds: 60 },
      defaults: { currencyCode: 'USD' },
    });
  });

  it('falls back to the values the server reports today when a group is missing', async () => {
    mockApi({ '/api/v2/meta/limits': { calendar: { maxRangeDays: 371 }, tasks: {} } });
    const { result } = renderHook(() => useLimits(), { wrapper: wrapperFor() });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toMatchObject(FALLBACK_LIMITS);
  });
});
