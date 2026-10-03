import { describe, expect, it } from 'vitest';
import {
  CLAIM_LOCK_NAME,
  CLAIM_STALE_MS,
  claimKey,
  claimMoment,
  pruneClaims,
  type ClaimDeps,
  type ClaimLocks,
  type ClaimStorage,
} from './notificationClaim.ts';

const PERSON = 'a00000000000000000000001';
const MOMENT = { dayKey: '2026-09-16', time: '08:00' };
const KEY = claimKey(PERSON, MOMENT);

class FakeStorage implements ClaimStorage {
  readonly data = new Map<string, string>();
  get length() {
    return this.data.size;
  }
  key(index: number) {
    return [...this.data.keys()][index] ?? null;
  }
  getItem(key: string) {
    return this.data.get(key) ?? null;
  }
  setItem(key: string, value: string) {
    this.data.set(key, value);
  }
  removeItem(key: string) {
    this.data.delete(key);
  }
}

/** Mutual exclusion like navigator.locks: one holder at a time, shared by all tabs. */
class FakeLocks implements ClaimLocks {
  readonly names: string[] = [];
  private tail: Promise<unknown> = Promise.resolve();
  request<T>(name: string, callback: () => Promise<T> | T): Promise<T> {
    this.names.push(name);
    const run = this.tail.then(async () => {
      // Yield so a missing lock would let another tab interleave here.
      await Promise.resolve();
      return callback();
    });
    this.tail = run.catch(() => undefined);
    return run;
  }
}

const tab = (shared: Partial<ClaimDeps>, token: string): ClaimDeps => ({
  now: () => new Date('2026-09-16T06:00:00Z'),
  token: () => token,
  ...shared,
});

describe('claimMoment with the Web Locks API', () => {
  it('lets exactly one of two tabs deliver', async () => {
    const storage = new FakeStorage();
    const locks = new FakeLocks();
    const results = await Promise.all([
      claimMoment(KEY, tab({ storage, locks }, 'tab-a')),
      claimMoment(KEY, tab({ storage, locks }, 'tab-b')),
    ]);
    expect(results.filter(Boolean)).toHaveLength(1);
    expect(locks.names).toEqual([CLAIM_LOCK_NAME, CLAIM_LOCK_NAME]);
    expect(JSON.parse(storage.getItem(KEY)!)).toMatchObject({ at: '2026-09-16T06:00:00.000Z' });
  });

  it('does not claim a moment again once it is claimed', async () => {
    const storage = new FakeStorage();
    const locks = new FakeLocks();
    expect(await claimMoment(KEY, tab({ storage, locks }, 'tab-a'))).not.toBeNull();
    expect(await claimMoment(KEY, tab({ storage, locks }, 'tab-a'))).toBeNull();
  });

  it('claims other moments, days and people independently', async () => {
    const storage = new FakeStorage();
    const locks = new FakeLocks();
    const keys = [
      KEY,
      claimKey(PERSON, { ...MOMENT, time: '18:30' }),
      claimKey(PERSON, { ...MOMENT, dayKey: '2026-09-17' }),
      claimKey('b00000000000000000000002', MOMENT),
    ];
    for (const key of keys) expect(await claimMoment(key, tab({ storage, locks }, 'tab-a'))).not.toBeNull();
    expect(storage.length).toBe(4);
  });

  it('gives a released moment back to the next check', async () => {
    const storage = new FakeStorage();
    const locks = new FakeLocks();
    const first = await claimMoment(KEY, tab({ storage, locks }, 'tab-a'));
    first!.release();
    expect(storage.getItem(KEY)).toBeNull();
    expect(await claimMoment(KEY, tab({ storage, locks }, 'tab-b'))).not.toBeNull();
  });

  it("does not release another tab's claim", async () => {
    const storage = new FakeStorage();
    const locks = new FakeLocks();
    const first = await claimMoment(KEY, tab({ storage, locks }, 'tab-a'));
    storage.setItem(KEY, JSON.stringify({ token: 'tab-b', at: '2026-09-16T06:00:01.000Z' }));
    first!.release();
    expect(storage.getItem(KEY)).not.toBeNull();
  });
});

describe('claimMoment without the Web Locks API', () => {
  const noWait = async () => undefined;

  it('lets the tab that wrote first win when the other sees the claim', async () => {
    const storage = new FakeStorage();
    const a = claimMoment(KEY, tab({ storage, locks: null, wait: noWait }, 'tab-a'));
    const b = claimMoment(KEY, tab({ storage, locks: null, wait: noWait }, 'tab-b'));
    const results = await Promise.all([a, b]);
    expect(results.filter(Boolean)).toHaveLength(1);
    expect(results[0]).not.toBeNull();
  });

  it('lets exactly one tab win when both check at the same moment', async () => {
    const storage = new FakeStorage();
    // Tab B read the key before tab A wrote it, so both believe the moment is free.
    const staleB: ClaimStorage = {
      get length() {
        return storage.length;
      },
      key: (index) => storage.key(index),
      getItem: (() => {
        let first = true;
        return (key: string) => {
          if (first) {
            first = false;
            return null;
          }
          return storage.getItem(key);
        };
      })(),
      setItem: (key, value) => storage.setItem(key, value),
      removeItem: (key) => storage.removeItem(key),
    };
    const a = claimMoment(KEY, tab({ storage, locks: null, wait: noWait }, 'tab-a'));
    const b = claimMoment(KEY, tab({ storage: staleB, locks: null, wait: noWait }, 'tab-b'));
    const results = await Promise.all([a, b]);
    expect(results.filter(Boolean)).toHaveLength(1);
    // Tab B wrote last, so its token is the one that survives.
    expect(results[1]).not.toBeNull();
    expect(results[0]).toBeNull();
  });

  it('waits before reading its own token back', async () => {
    const storage = new FakeStorage();
    const waits: number[] = [];
    const claim = await claimMoment(KEY, {
      ...tab({ storage, locks: null }, 'tab-a'),
      wait: async (ms) => void waits.push(ms),
    });
    expect(claim).not.toBeNull();
    expect(waits).toEqual([100]);
  });

  it('keeps a claim that was already there', async () => {
    const storage = new FakeStorage();
    storage.setItem(KEY, JSON.stringify({ token: 'tab-a', at: '2026-09-16T06:00:00.000Z' }));
    expect(await claimMoment(KEY, tab({ storage, locks: null, wait: noWait }, 'tab-b'))).toBeNull();
  });
});

describe('claimMoment without shared storage', () => {
  it('still never repeats within the tab', async () => {
    const key = claimKey(PERSON, { dayKey: '2031-01-01', time: '09:00' });
    const first = await claimMoment(key, { storage: null, locks: null });
    expect(first).not.toBeNull();
    expect(await claimMoment(key, { storage: null, locks: null })).toBeNull();
    first!.release();
    expect(await claimMoment(key, { storage: null, locks: null })).not.toBeNull();
  });
});

describe('pruneClaims', () => {
  it('removes claims older than seven days and leaves everything else', () => {
    const storage = new FakeStorage();
    const old = claimKey(PERSON, { dayKey: '2026-09-08', time: '08:00' });
    const edge = claimKey(PERSON, { dayKey: '2026-09-09', time: '08:00' });
    const recent = claimKey(PERSON, { dayKey: '2026-09-16', time: '08:00' });
    for (const key of [old, edge, recent, 'huishoudplanner.profileId', 'khc.other.2020-01-01']) storage.setItem(key, 'x');
    expect(pruneClaims('2026-09-16', storage)).toBe(1);
    expect([...storage.data.keys()].sort()).toEqual([edge, recent, 'huishoudplanner.profileId', 'khc.other.2020-01-01'].sort());
  });

  it('copes with unavailable storage', () => {
    expect(pruneClaims('2026-09-16', null)).toBe(0);
  });
});

describe('pending and done claims', () => {
  const at = (ms: number) => () => new Date(Date.parse('2026-09-16T06:00:00Z') + ms);

  it('keeps a pending claim for the stale period, then lets another tab take it over', async () => {
    const storage = new FakeStorage();
    const locks = new FakeLocks();
    const first = await claimMoment(KEY, tab({ storage, locks, now: at(0) }, 'tab-a'));
    expect(first!.holds()).toBe(true);

    expect(await claimMoment(KEY, tab({ storage, locks, now: at(CLAIM_STALE_MS - 1) }, 'tab-b'))).toBeNull();
    const second = await claimMoment(KEY, tab({ storage, locks, now: at(CLAIM_STALE_MS) }, 'tab-b'));
    expect(second).not.toBeNull();
    // The tab that was frozen mid-lookup notices the takeover and must stay silent.
    expect(first!.holds()).toBe(false);
    expect(second!.holds()).toBe(true);
  });

  it('takes over a stale claim without the Web Locks API too', async () => {
    const storage = new FakeStorage();
    const wait = async () => undefined;
    await claimMoment(KEY, tab({ storage, locks: null, wait, now: at(0) }, 'tab-a'));
    const second = await claimMoment(KEY, tab({ storage, locks: null, wait, now: at(CLAIM_STALE_MS + 1) }, 'tab-b'));
    expect(second).not.toBeNull();
  });

  it('never takes over a claim that is done, however old', async () => {
    const storage = new FakeStorage();
    const locks = new FakeLocks();
    const first = await claimMoment(KEY, tab({ storage, locks, now: at(0) }, 'tab-a'));
    first!.complete();
    expect(await claimMoment(KEY, tab({ storage, locks, now: at(10 * CLAIM_STALE_MS) }, 'tab-b'))).toBeNull();
    // A completed claim stays held by its owner.
    expect(first!.holds()).toBe(true);
  });

  it('does not let a replaced holder complete or release the new claim', async () => {
    const storage = new FakeStorage();
    const locks = new FakeLocks();
    const first = await claimMoment(KEY, tab({ storage, locks, now: at(0) }, 'tab-a'));
    const second = await claimMoment(KEY, tab({ storage, locks, now: at(CLAIM_STALE_MS) }, 'tab-b'));
    first!.complete();
    first!.release();
    expect(second!.holds()).toBe(true);
    expect(JSON.parse(storage.getItem(KEY)!)).toMatchObject({ token: 'tab-b', done: false });
  });
});

describe('claimMoment with storage that throws', () => {
  const broken = (): ClaimStorage => ({
    length: 0,
    key: () => {
      throw new Error('blocked');
    },
    getItem: () => {
      throw new Error('blocked');
    },
    setItem: () => {
      throw new Error('quota');
    },
    removeItem: () => {
      throw new Error('blocked');
    },
  });

  it('falls back to a claim that holds within the tab, with and without locks', async () => {
    for (const locks of [null, new FakeLocks()]) {
      const key = claimKey(PERSON, { dayKey: `2032-01-0${locks ? 1 : 2}`, time: '09:00' });
      const first = await claimMoment(key, { storage: broken(), locks, wait: async () => undefined });
      expect(first).not.toBeNull();
      expect(first!.holds()).toBe(true);
      first!.complete();
      expect(await claimMoment(key, { storage: broken(), locks, wait: async () => undefined })).toBeNull();
    }
  });

  it('survives a storage that fails when completing or releasing', async () => {
    const storage = new FakeStorage();
    const claim = await claimMoment(KEY, tab({ storage, locks: new FakeLocks() }, 'tab-a'));
    storage.setItem = () => {
      throw new Error('quota');
    };
    storage.removeItem = () => {
      throw new Error('blocked');
    };
    expect(() => claim!.complete()).not.toThrow();
    expect(() => claim!.release()).not.toThrow();
  });

  it('prunes nothing instead of throwing', () => {
    expect(pruneClaims('2026-09-16', broken())).toBe(0);
  });
});
