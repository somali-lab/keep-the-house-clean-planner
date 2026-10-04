import { QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { LIMITS, calendarRoute, mockApi, testQueryClient } from '../../test/fixtures.ts';
import { useCalendar, useLimits, useRooms, useTasks } from './queries.ts';

function wrapperFor(queryClient = testQueryClient()) {
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}

describe('useLimits', () => {
  it('reads the limits once per session, however many components ask', async () => {
    const fetchMock = mockApi({ '/api/v2/meta/limits': LIMITS });
    const wrapper = wrapperFor();
    const first = renderHook(() => useLimits(), { wrapper });
    const second = renderHook(() => useLimits(), { wrapper });
    await waitFor(() => expect(first.result.current.isSuccess).toBe(true));
    await waitFor(() => expect(second.result.current.isSuccess).toBe(true));

    expect(first.result.current.data?.tasks).toMatchObject({ minPoints: 0, maxPoints: 1000, skipReasonMaxLength: 500 });
    expect(fetchMock.mock.calls.filter(([url]) => url === '/api/v2/meta/limits')).toHaveLength(1);
  });

  it('is never stale, so a remount does not read it again', async () => {
    const fetchMock = mockApi({ '/api/v2/meta/limits': LIMITS });
    const wrapper = wrapperFor();
    const first = renderHook(() => useLimits(), { wrapper });
    await waitFor(() => expect(first.result.current.isSuccess).toBe(true));
    first.unmount();
    const again = renderHook(() => useLimits(), { wrapper });
    expect(again.result.current.data).toBeDefined();
    expect(fetchMock.mock.calls.filter(([url]) => url === '/api/v2/meta/limits')).toHaveLength(1);
  });
});

describe('useCalendar', () => {
  it('maps every day of the range to its position in the cycles', async () => {
    const fetchMock = mockApi({ '/api/v2/calendar': calendarRoute('2026-09-14') });
    const { result } = renderHook(() => useCalendar('2026-09-13', '2026-09-22'), { wrapper: wrapperFor() });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(fetchMock.mock.calls[0]![0]).toBe('/api/v2/calendar?from=2026-09-13&to=2026-09-22');
    const days = result.current.data!;
    expect(days.size).toBe(10);
    expect(days.get('2026-09-13')).toMatchObject({ cycleIndex: -1 });
    expect(days.get('2026-09-14')).toMatchObject({ cycleIndex: 0, weekIndex: 0, weekStart: '2026-09-14' });
    expect(days.get('2026-09-22')).toMatchObject({ cycleIndex: 0, weekIndex: 1, weekStart: '2026-09-21' });
  });
});

describe('useRooms and useTasks', () => {
  const room = (id: string, extra = {}) => ({ id, name: `Kamer ${id}`, sortOrder: '10', active: true, virtual: false, createdAt: 'x', updatedAt: 'x', version: '3', ...extra });
  const task = (id: string, extra = {}) => ({
    id, name: `Taak ${id}`, roomId: 'r1', intervalKey: '1w', durationMinutes: '20', points: '15', defaultAssigneeId: null,
    active: true, notes: '', tags: [], lastCompletedAt: null, createdAt: 'x', updatedAt: 'x', version: 4, ...extra,
  });

  it('reads every page of the rooms and maps the numbers', async () => {
    const fetchMock = mockApi({
      '/api/v2/rooms': (_init: RequestInit | undefined, url: string) =>
        new URL(url, 'http://localhost').searchParams.get('cursor') === 'c2'
          ? { items: [room('r3')], nextCursor: null }
          : { items: [room('r1'), room('r2')], nextCursor: 'c2' },
    });
    const { result } = renderHook(() => useRooms(), { wrapper: wrapperFor() });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data!.map((r) => r.id)).toEqual(['r1', 'r2', 'r3']);
    expect(result.current.data![0]).toMatchObject({ sortOrder: 10, active: true, version: 3 });
    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual(['/api/v2/rooms?limit=200', '/api/v2/rooms?limit=200&cursor=c2']);
  });

  it('reads every page of the tasks, with the points the server computed', async () => {
    const fetchMock = mockApi({
      '/api/v2/tasks': (_init: RequestInit | undefined, url: string) =>
        new URL(url, 'http://localhost').searchParams.get('cursor') === 'c2'
          ? { items: [task('t2', { points: 0 })], nextCursor: null }
          : { items: [task('t1')], nextCursor: 'c2' },
    });
    const { result } = renderHook(() => useTasks(), { wrapper: wrapperFor() });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data!.map((t) => [t.id, t.durationMinutes, t.points, t.version])).toEqual([['t1', 20, 15, 4], ['t2', 20, 0, 4]]);
    expect(fetchMock.mock.calls[0]![0]).toBe('/api/v2/tasks?limit=200');
  });

  it('is invalidated by the key every task mutation of the app already uses', async () => {
    const fetchMock = mockApi({ '/api/v2/tasks': { items: [task('t1')], nextCursor: null } });
    const queryClient = testQueryClient();
    const { result } = renderHook(() => useTasks(), { wrapper: wrapperFor(queryClient) });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    await queryClient.invalidateQueries({ queryKey: ['tasks'] });
    await waitFor(() => expect(fetchMock.mock.calls.length).toBe(2));
  });
});
