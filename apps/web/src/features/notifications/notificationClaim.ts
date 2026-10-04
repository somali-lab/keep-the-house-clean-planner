import { addDays } from '@/lib/dayKey';
import type { NotificationMoment } from './notificationModel.ts';

/**
 * At most one summary per person, day and moment, however many tabs are open.
 * A moment is claimed in localStorage, which every tab of the browser profile
 * shares. The Web Locks API makes check-and-set atomic across tabs; without it
 * a random token is written, read back after a short wait, and only the tab
 * whose token survived delivers.
 *
 * A claim is written before the lookup of the person's tasks, so it is
 * pending until its tab has shown the notification (or decided there is
 * nothing to show) and marked it done. A pending claim older than
 * CLAIM_STALE_MS is taken over by the next check of any tab, so a tab that
 * was frozen or closed mid-lookup does not swallow the moment; the old
 * holder notices the takeover (holds()) and stays silent.
 */

export const CLAIM_LOCK_NAME = 'khc-browser-notify';
export const CLAIM_PREFIX = 'khc.notified.';
export const CLAIM_RETENTION_DAYS = 7;
export const CLAIM_FALLBACK_DELAY_MS = 100;
export const CLAIM_STALE_MS = 60_000;

export interface ClaimStorage {
  readonly length: number;
  key(index: number): string | null;
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export interface ClaimLocks {
  request<T>(name: string, callback: () => Promise<T> | T): Promise<T>;
}

export interface ClaimDeps {
  /** Null forces the localStorage-only fallback. Default: navigator.locks. */
  locks?: ClaimLocks | null;
  /** Default: window.localStorage. */
  storage?: ClaimStorage | null;
  now?: () => Date;
  wait?: (ms: number) => Promise<void>;
  token?: () => string;
  fallbackDelayMs?: number;
}

export interface Claim {
  /** Whether this tab still owns the claim; false once another tab took over a stale one. */
  holds(): boolean;
  /** Marks the moment as handled (shown, or nothing to show); a done claim is never taken over. */
  complete(): void;
  /** Gives the moment back, so a later check can try again (e.g. after a failed fetch). */
  release(): void;
}

interface StoredClaim {
  token?: unknown;
  at?: unknown;
  done?: unknown;
}

export function claimKey(personId: string, moment: Pick<NotificationMoment, 'dayKey' | 'time'>): string {
  return `${CLAIM_PREFIX}${personId}.${moment.dayKey}.${moment.time}`;
}

const memoryClaims = new Set<string>();

function defaultStorage(): ClaimStorage | null {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}

function defaultLocks(): ClaimLocks | null {
  const locks = typeof navigator === 'undefined' ? undefined : (navigator as Navigator & { locks?: ClaimLocks }).locks;
  return locks && typeof locks.request === 'function' ? locks : null;
}

const defaultWait = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));
const defaultToken = () =>
  typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;

function memoryClaim(key: string): Claim | null {
  // Without shared storage the tabs cannot coordinate; at least never repeat within this one.
  if (memoryClaims.has(key)) return null;
  memoryClaims.add(key);
  return {
    holds: () => memoryClaims.has(key),
    complete: () => undefined,
    release: () => void memoryClaims.delete(key),
  };
}

/** True when the stored claim is done, or pending and still fresh; garbage counts as a done claim. */
function isLive(stored: string | null, nowMs: number): boolean {
  if (stored === null) return false;
  try {
    const claim = JSON.parse(stored) as StoredClaim;
    if (claim.done === true) return true;
    const at = typeof claim.at === 'string' ? Date.parse(claim.at) : Number.NaN;
    return Number.isNaN(at) || nowMs - at < CLAIM_STALE_MS;
  } catch {
    return true;
  }
}

/**
 * Claims the moment for this tab; null when another tab (or an earlier check)
 * already did. Storage that throws (blocked, full) falls back to a claim that
 * only holds within this tab.
 */
export async function claimMoment(key: string, deps: ClaimDeps = {}): Promise<Claim | null> {
  const storage = deps.storage === undefined ? defaultStorage() : deps.storage;
  if (!storage) return memoryClaim(key);
  try {
    return await claimInStorage(key, storage, deps);
  } catch {
    return memoryClaim(key);
  }
}

async function claimInStorage(key: string, storage: ClaimStorage, deps: ClaimDeps): Promise<Claim | null> {
  const locks = deps.locks === undefined ? defaultLocks() : deps.locks;
  const now = deps.now ?? (() => new Date());
  const token = (deps.token ?? defaultToken)();
  const write = (done: boolean) => storage.setItem(key, JSON.stringify({ token, at: now().toISOString(), done }));

  const holds = () => {
    try {
      const stored = storage.getItem(key);
      return stored !== null && (JSON.parse(stored) as StoredClaim).token === token;
    } catch {
      return false;
    }
  };
  const claim: Claim = {
    holds,
    complete: () => {
      try {
        if (holds()) write(true);
      } catch {
        // The claim stays pending and goes stale; nothing more to do.
      }
    },
    release: () => {
      try {
        if (holds()) storage.removeItem(key);
      } catch {
        // A stale pending claim is taken over by the next check anyway.
      }
    },
  };

  if (locks) {
    return locks.request(CLAIM_LOCK_NAME, () => {
      if (isLive(storage.getItem(key), now().getTime())) return null;
      write(false);
      return claim;
    });
  }

  if (isLive(storage.getItem(key), now().getTime())) return null;
  write(false);
  await (deps.wait ?? defaultWait)(deps.fallbackDelayMs ?? CLAIM_FALLBACK_DELAY_MS);
  return holds() ? claim : null;
}

const CLAIM_KEY_RE = /^khc\.notified\.[^.]+\.(\d{4}-\d{2}-\d{2})\.\d{2}:\d{2}$/;

/** Removes claims for days older than the retention period; other keys are left alone. */
export function pruneClaims(todayKey: string, storage: ClaimStorage | null = defaultStorage()): number {
  if (!storage) return 0;
  try {
    const oldest = addDays(todayKey, -CLAIM_RETENTION_DAYS);
    const stale: string[] = [];
    for (let i = 0; i < storage.length; i++) {
      const key = storage.key(i);
      const dayKey = key ? CLAIM_KEY_RE.exec(key)?.[1] : undefined;
      if (key && dayKey && dayKey < oldest) stale.push(key);
    }
    for (const key of stale) storage.removeItem(key);
    return stale.length;
  } catch {
    return 0;
  }
}
