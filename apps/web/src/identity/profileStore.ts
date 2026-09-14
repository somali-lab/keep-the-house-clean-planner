/**
 * The only code that reads or writes the chosen profile. Kept in memory and
 * mirrored to localStorage when available (private mode may throw).
 */

const STORAGE_KEY = 'huishoudplanner.profileId';

let current: string | null | undefined;

function readStorage(): string | null {
  try {
    return window.localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

function writeStorage(id: string | null): void {
  try {
    if (id === null) window.localStorage.removeItem(STORAGE_KEY);
    else window.localStorage.setItem(STORAGE_KEY, id);
  } catch {
    // Storage unavailable: the choice lasts for this session only.
  }
}

export function getActiveProfileId(): string | null {
  if (current === undefined) current = readStorage();
  return current;
}

export function setActiveProfileId(id: string | null): void {
  current = id;
  writeStorage(id);
}

/** Tests only: forget the in-memory value so the next read hits storage again. */
export function resetProfileStore(): void {
  current = undefined;
}
