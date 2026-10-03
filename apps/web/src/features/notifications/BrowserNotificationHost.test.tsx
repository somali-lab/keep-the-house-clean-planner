import { act } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, makeUser, mockApi, storeProfile, testQueryClient } from '../../test/fixtures.ts';
import { makeOccurrence, makeSettings, renderWithProviders } from '../../test/render.tsx';
import { BrowserNotificationHost, deliverMoment } from './BrowserNotificationHost.tsx';
import { CLAIM_STALE_MS, claimKey } from './notificationClaim.ts';

interface Shown {
  title: string;
  body?: string;
  tag?: string;
}

let shown: Shown[];
let permission: NotificationPermission;

function stubNotification() {
  shown = [];
  permission = 'granted';
  class FakeNotification {
    static get permission() {
      return permission;
    }
    static requestPermission = vi.fn(async () => permission);
    constructor(title: string, options?: NotificationOptions) {
      shown.push({ title, ...(options?.body ? { body: options.body } : {}), ...(options?.tag ? { tag: options.tag } : {}) });
    }
  }
  vi.stubGlobal('Notification', FakeNotification);
}

const ME = makeUser({
  ...ANNA,
  browserNotifications: { enabled: true, times: ['10:00'] },
});
const TODAY = '2026-09-16';
const OPEN_TODAY = makeOccurrence({ _id: 'e00000000000000000000001', assigneeId: ME._id, date: TODAY, taskNameSnapshot: 'Afwassen' });
const OPEN_OVERDUE = makeOccurrence({ _id: 'e00000000000000000000002', assigneeId: ME._id, date: '2026-09-14', taskNameSnapshot: 'Dweilen' });

beforeEach(stubNotification);
afterEach(() => vi.useRealTimers());

describe('deliverMoment', () => {
  const moment = { dayKey: TODAY, time: '10:00', at: new Date('2026-09-16T08:00:00Z') };
  const tabs = () => ({ queryClient: testQueryClient(), inFlight: new Set<string>() });
  const occurrencesCalls = (fetchMock: ReturnType<typeof mockApi>) =>
    fetchMock.mock.calls.filter(([url]) => String(url).startsWith('/api/occurrences')).length;

  beforeEach(() => vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-09-16T08:00:30Z') }));

  it('shows one summary when two tabs reach the same moment', async () => {
    const fetchMock = mockApi({ '/api/occurrences': [OPEN_TODAY, OPEN_OVERDUE] });
    // Two tabs share localStorage but have their own query client and in-flight set.
    await Promise.all([
      deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs()),
      deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs()),
    ]);
    expect(shown).toEqual([
      {
        title: 'Keep the House Clean: 1 taak vandaag, 1 achterstallig',
        body: 'Afwassen, Dweilen',
        tag: claimKey(ME._id, moment),
      },
    ]);
    expect(occurrencesCalls(fetchMock)).toBe(1);
    expect(String(fetchMock.mock.calls[0]![0])).toBe(
      `/api/occurrences?from=2026-07-22&to=${TODAY}&status=open&assigneeId=${ME._id}`,
    );
  });

  it('does not repeat a moment that was already delivered', async () => {
    const fetchMock = mockApi({ '/api/occurrences': [OPEN_TODAY] });
    await deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    await deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    expect(shown).toHaveLength(1);
    expect(occurrencesCalls(fetchMock)).toBe(1);
  });

  it('sends nothing when the person has nothing open, and does not look again', async () => {
    const fetchMock = mockApi({ '/api/occurrences': [] });
    await deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    await deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    expect(shown).toEqual([]);
    expect(occurrencesCalls(fetchMock)).toBe(1);
  });

  it('claims nothing without permission, so granting it within the grace period still delivers', async () => {
    const fetchMock = mockApi({ '/api/occurrences': [OPEN_TODAY] });
    permission = 'default';
    await deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    expect(shown).toEqual([]);
    expect(occurrencesCalls(fetchMock)).toBe(0);
    permission = 'granted';
    await deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    expect(shown).toHaveLength(1);
  });

  it('lets another tab deliver when the tab holding the claim hangs, and keeps the hung tab silent', async () => {
    let calls = 0;
    let release: (value: unknown[]) => void = () => undefined;
    const hung = new Promise<unknown[]>((resolve) => (release = resolve));
    mockApi({ '/api/occurrences': () => (++calls === 1 ? hung : [OPEN_TODAY]) });

    const hungTab = deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    await vi.waitFor(() => expect(calls).toBe(1));
    // Inside the stale period the pending claim blocks other tabs.
    await deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    expect(calls).toBe(1);
    expect(shown).toEqual([]);

    vi.setSystemTime(new Date(Date.parse('2026-09-16T08:00:30Z') + CLAIM_STALE_MS + 1_000));
    await deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    expect(shown).toHaveLength(1);

    release([OPEN_TODAY]);
    await hungTab;
    expect(shown).toHaveLength(1);
  });

  it('gives the moment back when the lookup fails, so the next check retries', async () => {
    let failing = true;
    mockApi({
      '/api/occurrences': () => {
        if (failing) throw new Error('offline');
        return [OPEN_TODAY];
      },
    });
    await deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    expect(shown).toEqual([]);
    failing = false;
    await deliverMoment(ME._id, 'Europe/Amsterdam', moment, tabs());
    expect(shown).toHaveLength(1);
  });
});

describe('BrowserNotificationHost', () => {
  const NOW = '2026-09-16T07:59:30Z';

  function setup(profile = ME) {
    storeProfile(profile._id);
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'], now: new Date(NOW) });
    const fetchMock = mockApi({
      '/api/users': [profile, BRAM],
      '/api/settings': makeSettings(),
      '/api/occurrences': [OPEN_TODAY],
    });
    renderWithProviders(<BrowserNotificationHost />);
    return fetchMock;
  }
  const advance = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms));

  it('shows the summary when the configured moment is reached, and only once', async () => {
    setup();
    await advance(1_000);
    expect(shown).toEqual([]);
    await advance(29_000);
    await advance(2_000);
    expect(shown).toHaveLength(1);
    expect(shown[0]!.title).toBe('Keep the House Clean: 1 taak vandaag');
    await advance(11 * 60_000);
    expect(shown).toHaveLength(1);
  });

  it('does not catch up on a moment that passed more than ten minutes ago', async () => {
    const fetchMock = setup();
    await advance(1_000);
    vi.setSystemTime(new Date('2026-09-16T08:15:00Z'));
    act(() => void document.dispatchEvent(new Event('visibilitychange')));
    await advance(1_000);
    expect(shown).toEqual([]);
    expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith('/api/occurrences'))).toBe(false);
  });

  it('delivers a moment within the grace period when the tab is restored', async () => {
    setup();
    await advance(1_000);
    vi.setSystemTime(new Date('2026-09-16T08:05:00Z'));
    act(() => void document.dispatchEvent(new Event('visibilitychange')));
    await advance(1_000);
    expect(shown).toHaveLength(1);
  });

  it.each(['denied', 'unsupported', 'insecure'] as const)('does not run the scheduler when permission is %s', async (state) => {
    if (state === 'denied') permission = 'denied';
    if (state === 'unsupported') vi.unstubAllGlobals();
    if (state === 'insecure') Object.defineProperty(window, 'isSecureContext', { configurable: true, value: false });
    try {
      const fetchMock = setup();
      await advance(120_000);
      expect(shown).toEqual([]);
      expect(fetchMock.mock.calls.some(([url]) => String(url) === '/api/settings')).toBe(false);
      expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith('/api/occurrences'))).toBe(false);
    } finally {
      Reflect.deleteProperty(window, 'isSecureContext');
    }
  });

  it('starts when permission is granted later and the tab is restored', async () => {
    permission = 'denied';
    const fetchMock = setup();
    await advance(1_000);
    expect(fetchMock.mock.calls.some(([url]) => String(url) === '/api/settings')).toBe(false);

    permission = 'granted';
    vi.setSystemTime(new Date('2026-09-16T08:02:00Z'));
    act(() => void document.dispatchEvent(new Event('visibilitychange')));
    await advance(1_000);
    expect(shown).toHaveLength(1);
  });

  it('does nothing and fetches no occurrences when the profile has notifications off', async () => {
    const fetchMock = setup(makeUser({ ...ANNA, browserNotifications: { enabled: false, times: ['10:00'] } }));
    await advance(60_000);
    expect(shown).toEqual([]);
    expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith('/api/occurrences'))).toBe(false);
    expect(fetchMock.mock.calls.some(([url]) => String(url) === '/api/settings')).toBe(false);
  });
});
