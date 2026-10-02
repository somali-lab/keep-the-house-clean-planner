import { useCallback, useState, type Dispatch, type SetStateAction } from 'react';
import { getActiveProfileId } from '../identity/profileStore.ts';

const PREFIX = 'huishoudplanner.filters';

function readValue<T>(key: string, fallback: T): T {
  try {
    const stored = window.localStorage.getItem(key);
    return stored === null ? fallback : JSON.parse(stored) as T;
  } catch {
    return fallback;
  }
}

/** Keeps one filter per selected profile so a household member never inherits another's view. */
export function usePersistedFilter<T>(
  key: string,
  profileId: string | null,
  initialValue: T,
): [T, Dispatch<SetStateAction<T>>, () => void] {
  // The profile query can still be loading on the first render. Its selected ID
  // is already available in the profile store, so avoid a transient guest filter.
  const storageKey = `${PREFIX}.${profileId ?? getActiveProfileId() ?? 'guest'}.${key}`;
  const [state, setState] = useState(() => ({ key: storageKey, value: readValue(storageKey, initialValue) }));
  const value = state.key === storageKey ? state.value : readValue(storageKey, initialValue);

  const setValue: Dispatch<SetStateAction<T>> = useCallback((update) => {
    const next = typeof update === 'function'
      ? (update as (previous: T) => T)(value)
      : update;
    try {
      window.localStorage.setItem(storageKey, JSON.stringify(next));
    } catch {
      // The filter remains usable in this tab if browser storage is unavailable.
    }
    setState({ key: storageKey, value: next });
  }, [storageKey, value]);

  const reset = useCallback(() => {
    try {
      window.localStorage.removeItem(storageKey);
    } catch {
      // Reset the visible filter even if browser storage is unavailable.
    }
    setState({ key: storageKey, value: initialValue });
  }, [initialValue, storageKey]);

  return [value, setValue, reset];
}
