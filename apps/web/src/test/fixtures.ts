import type { User } from '@huishoudplanner/shared';
import { QueryClient } from '@tanstack/react-query';
import { vi } from 'vitest';
import type { Badge } from '../features/badges/api.ts';
import type { RewardProgress } from '../features/reward/api.ts';

const STAMP = '2026-09-14T08:00:00.000Z';

export function makeUser(overrides: Partial<User> & Pick<User, '_id' | 'name'>): User {
  return {
    color: '#2563eb',
    active: true,
    role: 'member',
    unavailableWeekdays: [],
    dailyBudgetMinutes: { weekday: 60, weekend: 120 },
    maxDailyMinutes: { weekday: 480, weekend: 480 },
    browserNotifications: { enabled: false, times: [] },
    createdAt: STAMP,
    updatedAt: STAMP,
    ...overrides,
  };
}

export const ANNA = makeUser({ _id: 'a00000000000000000000001', name: 'Anna', color: '#2563eb', role: 'admin' });
export const BRAM = makeUser({ _id: 'b00000000000000000000002', name: 'Bram de Vries', color: '#db2777' });

export type RouteHandler = unknown | ((init: RequestInit | undefined, url: string) => unknown);

/**
 * What a handler sees of a request. The generated client (openapi-fetch) hands fetch a `Request`; the v1 client a url
 * and an init. Both are reduced to a path with its query, and an init with the method, headers and text body.
 */
export async function describeRequest(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<{ url: string; init: RequestInit | undefined }> {
  if (typeof input === 'string' || input instanceof URL) return { url: String(input), init };
  const parsed = new URL(input.url);
  const body = input.method === 'GET' || input.method === 'HEAD' ? undefined : await input.clone().text();
  return {
    url: `${parsed.pathname}${parsed.search}`,
    init: {
      method: input.method,
      headers: Object.fromEntries(input.headers.entries()),
      ...(body ? { body } : {}),
    },
  };
}

/**
 * Stubs global fetch with a route table keyed by `METHOD /path` (or just `/path` for GET).
 * Handlers may be values or functions; unknown routes return 404. A handler that returns a `Response` answers with it
 * (for example a problem+json error); a thrown error is a connection that fails.
 */
export function mockApi(routes: Record<string, RouteHandler>) {
  // The mock records what a handler sees (a path and an init), whichever client made the request.
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const path = url.split('?')[0]!;
    const method = init?.method ?? 'GET';
    const handler = routes[`${method} ${path}`] ?? (method === 'GET' ? routes[path] : undefined);
    if (handler === undefined) {
      return new Response(JSON.stringify({ code: 'not_found' }), { status: 404 });
    }
    const body = typeof handler === 'function' ? await handler(init, url) : handler;
    if (body instanceof Response) return body;
    return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
  });
  vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
    const described = await describeRequest(input, init);
    return fetchMock(described.url, described.init);
  });
  return fetchMock;
}

/** A page of a bounded v2 list. */
export const page = <T,>(items: T[]) => ({ items, nextCursor: null });

/** A problem+json answer of the v2 API; `code` is the part after `urn:huishoudplanner:problem:`. */
export function problem(status: number, code: string, detail = code, extra: Record<string, unknown> = {}): Response {
  return new Response(
    JSON.stringify({ type: `urn:huishoudplanner:problem:${code}`, title: code, status, detail, traceId: 'trace', ...extra }),
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

/** `GET /api/v2/meta/limits` as the server answers it (the values the web app reads). */
export const LIMITS = {
  calendar: { cycleDays: 28, cycleWeeks: 4, planWeeks: 4, maxRangeDays: 371 },
  tasks: { minPoints: 0, maxPoints: 1000, minDurationMinutes: 1, oneOffNameMaxLength: 120, intervalKeyMaxLength: 32, skipReasonMaxLength: 500 },
  points: { minCentsPerPoint: 0, maxCentsPerPoint: 10000, maxRedemptionNoteLength: 200, maxEntriesRangeDays: 371, maxCorrections: 100 },
  badges: {
    minNameLength: 1,
    maxNameLength: 60,
    maxDescriptionLength: 200,
    maxImageBytes: 256 * 1024,
    imageTypes: ['image/png', 'image/jpeg', 'image/webp'],
    maxThreshold: 100_000,
    maxOnTimeWeeksThreshold: 1000,
    maxBadges: 100,
    maxRuleTasks: 500,
  },
};

const DAY_MS = 86_400_000;
const utc = (dayKey: string) => Date.parse(`${dayKey}T00:00:00Z`);

/** The ISO week label of a day (`2026-W38`), as the server's calendar gives it; a stand-in for tests. */
function isoWeekLabel(time: number): string {
  const thursday = new Date(time + (3 - ((new Date(time).getUTCDay() + 6) % 7)) * DAY_MS);
  const year = thursday.getUTCFullYear();
  const week = Math.floor((thursday.getTime() - Date.UTC(year, 0, 1)) / (7 * DAY_MS)) + 1;
  return `${year}-W${String(week).padStart(2, '0')}`;
}

/**
 * `GET /api/v2/calendar` for a cycle that starts on `anchor` (a Monday): four weeks per cycle, and a negative cycle index
 * before the anchor. A stand-in for the server's calendar, so tests do not repeat its rule.
 */
export function calendarRoute(anchor = '2026-09-14') {
  return (_init: RequestInit | undefined, url: string) => {
    const query = new URL(url, 'http://localhost').searchParams;
    const from = utc(query.get('from') ?? '');
    const to = utc(query.get('to') ?? '');
    const days = [];
    for (let time = from; time <= to; time += DAY_MS) {
      const offset = Math.round((time - utc(anchor)) / DAY_MS);
      const date = new Date(time);
      const weekday = date.getUTCDay();
      days.push({
        dayKey: date.toISOString().slice(0, 10),
        weekday,
        cycleIndex: Math.floor(offset / 28),
        weekIndex: ((Math.floor(offset / 7) % 4) + 4) % 4,
        isoWeek: isoWeekLabel(time),
        weekStart: new Date(time - ((weekday + 6) % 7) * DAY_MS).toISOString().slice(0, 10),
      });
    }
    return { timezone: 'Europe/Amsterdam', days };
  };
}

/** The routes every page that reads the calendar or the limits needs. */
export const v2Basics = (anchor = '2026-09-14') => ({
  '/api/v2/meta/limits': LIMITS,
  '/api/v2/calendar': calendarRoute(anchor),
});

export function testQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
}

export function storeProfile(id: string): void {
  window.localStorage.setItem('huishoudplanner.profileId', id);
}

/** A badge as `GET /api/v2/badges` returns it (ADR-0014). */
export function makeBadge(overrides: Partial<Badge> & Pick<Badge, 'id' | 'name'>): Badge {
  return {
    description: '',
    rule: { type: 'executions', taskIds: [], threshold: 10 },
    active: true,
    exampleKey: null,
    image: null,
    createdAt: STAMP,
    updatedAt: STAMP,
    version: 1,
    ...overrides,
  };
}

/** Image details of a stored badge picture, with the address the server gives (`?v=` is the start of the hash). */
export function makeBadgeImage(id: string): NonNullable<Badge['image']> {
  return { contentType: 'image/png', size: 70, hash: 'a'.repeat(64), url: `/api/v2/badges/${id}/image?v=aaaaaaaaaaaa` };
}

/** The reward meter as `GET /api/v2/points/progress` returns it (requirements 4.12): a week with 3 of 4 points earned. */
export function makeProgress(overrides: Partial<RewardProgress> = {}): RewardProgress {
  // The server counts the eggs (one per full 10%); this stands in for it, so a test only states the percentage.
  const percent = overrides.percent ?? 75;
  return {
    personId: ANNA._id,
    period: 'week',
    start: '2026-09-14',
    end: '2026-09-20',
    earnedPoints: 3,
    goalPoints: 4,
    goalSource: 'automatic',
    percent,
    eggs: Math.floor(percent / 10),
    eggCount: 10,
    currencyCode: 'EUR',
    centsPerPoint: 0,
    money: null,
    ...overrides,
  };
}
