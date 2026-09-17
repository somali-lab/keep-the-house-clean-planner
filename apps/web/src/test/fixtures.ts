import type { User } from '@huishoudplanner/shared';
import { QueryClient } from '@tanstack/react-query';
import { vi } from 'vitest';

const STAMP = '2026-09-14T08:00:00.000Z';

export function makeUser(overrides: Partial<User> & Pick<User, '_id' | 'name'>): User {
  return {
    color: '#2563eb',
    active: true,
    role: 'member',
    unavailableWeekdays: [],
    dailyBudgetMinutes: { weekday: 60, weekend: 120 },
    maxDailyMinutes: { weekday: 480, weekend: 480 },
    createdAt: STAMP,
    updatedAt: STAMP,
    ...overrides,
  };
}

export const ANNA = makeUser({ _id: 'a00000000000000000000001', name: 'Anna', color: '#2563eb', role: 'admin' });
export const BRAM = makeUser({ _id: 'b00000000000000000000002', name: 'Bram de Vries', color: '#db2777' });

export type RouteHandler = unknown | ((init: RequestInit | undefined, url: string) => unknown);

/**
 * Stubs global fetch with a route table keyed by `METHOD /path` (or just `/path` for GET).
 * Handlers may be values or functions; unknown routes return 404.
 */
export function mockApi(routes: Record<string, RouteHandler>) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const path = url.split('?')[0]!;
    const method = init?.method ?? 'GET';
    const handler = routes[`${method} ${path}`] ?? (method === 'GET' ? routes[path] : undefined);
    if (handler === undefined) {
      return new Response(JSON.stringify({ code: 'not_found' }), { status: 404 });
    }
    const body = typeof handler === 'function' ? await handler(init, url) : handler;
    return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

export function testQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
}

export function storeProfile(id: string): void {
  window.localStorage.setItem('huishoudplanner.profileId', id);
}
