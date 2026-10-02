import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { setActiveProfileId } from '../identity/profileStore.ts';
import { usePersistedFilter } from './usePersistedFilter.ts';

describe('usePersistedFilter', () => {
  beforeEach(() => window.localStorage.clear());

  it('restores a filter after remount and keeps profiles separate', () => {
    const first = renderHook(({ profileId }) => usePersistedFilter('week.person', profileId, profileId), {
      initialProps: { profileId: 'anna' },
    });
    act(() => first.result.current[1]('all'));
    expect(window.localStorage.getItem('huishoudplanner.filters.anna.week.person')).toBe('"all"');
    first.rerender({ profileId: 'bram' });
    expect(first.result.current[0]).toBe('bram');
    act(() => first.result.current[1]('unassigned'));
    first.rerender({ profileId: 'anna' });
    expect(first.result.current[0]).toBe('all');
    first.unmount();

    const second = renderHook(() => usePersistedFilter('week.person', 'anna', 'anna'));
    expect(second.result.current[0]).toBe('all');
    act(() => second.result.current[2]());
    expect(second.result.current[0]).toBe('anna');
    expect(window.localStorage.getItem('huishoudplanner.filters.anna.week.person')).toBeNull();
  });

  it('uses the selected profile while its query is loading', () => {
    setActiveProfileId('anna');
    const filter = renderHook(() => usePersistedFilter('today.person', null, 'all'));
    act(() => filter.result.current[1]('unassigned'));
    expect(window.localStorage.getItem('huishoudplanner.filters.anna.today.person')).toBe('"unassigned"');
  });
});
