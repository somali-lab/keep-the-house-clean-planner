import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Occurrence } from '../api/index.ts';
import { applyOptimistic } from '../features/today/api.ts';
import { TodayPage } from '../features/today/TodayPage.tsx';
import { ANNA, BRAM, describeRequest, LIMITS, calendarRoute, page, problem, storeProfile } from '../test/fixtures.ts';
import { makeOccurrenceV2, makeSettings, renderWithProviders } from '../test/render.tsx';
import { OfflineSyncProvider } from './OfflineSyncProvider.tsx';
import { memoryStore, type QueueStore } from './queue.ts';

const NOW = new Date('2026-09-16T08:00:00Z');
const TODAY = '2026-09-16';
const PENDING_ONE = '1 wijziging is offline bewaard en wordt verstuurd zodra er weer verbinding is.';

type Mode = 'online' | 'offline' | 'conflict' | 'down' | 'busy' | 'refused' | 'applied';

let db: Occurrence[];
let mode: Mode;
let store: QueueStore;

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

/** A small server that can lose its connection, answer 404, or fail with 500. */
function stubServer() {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const described = await describeRequest(input, init);
    const url = described.url;
    const method = described.init?.method ?? 'GET';
    if (mode === 'offline') throw new TypeError('Failed to fetch');

    if (method === 'GET') {
      const path = url.split('?')[0]!;
      const one = /^\/api\/v2\/occurrences\/([^/]+)$/.exec(path);
      if (one) {
        const found = db.find((o) => o.id === one[1]);
        return found ? json(found) : problem(404, 'not_found');
      }
      const routes: Record<string, unknown> = {
        '/api/v2/users': page([ANNA, BRAM]),
        '/api/v2/settings': makeSettings(),
        '/api/v2/occurrences': page(db),
        '/api/v2/meta/limits': LIMITS,
        '/api/v2/calendar': calendarRoute()(undefined, url),
      };
      return path in routes ? json(routes[path]) : json({ code: 'not_found' }, 404);
    }

    if (mode === 'conflict') return problem(404, 'not_found');
    if (mode === 'down') return problem(500, 'internal_error');
    if (mode === 'busy') return problem(429, 'rate_limited');
    if (mode === 'refused') return problem(400, 'validation_error');
    if (mode === 'applied') return problem(409, 'invalid_transition');
    const match = /^\/api\/v2\/occurrences\/([^/]+)\/(complete|uncomplete|skip)$/.exec(url);
    if (method === 'POST' && match) {
      const current = db.find((o) => o.id === match[1])!;
      const profileId = (described.init!.headers as Record<string, string>)['x-profile-id']!;
      const next = applyOptimistic(current, { id: current.id, kind: match[2] as 'complete' }, { profileId, todayKey: TODAY, now: NOW });
      db = db.map((o) => (o.id === next.id ? next : o));
      return json(next);
    }
    return json({ code: 'not_found' }, 404);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const patchCalls = async (fetchMock: ReturnType<typeof stubServer>) => {
  const sent = [];
  for (const [input, init] of fetchMock.mock.calls) {
    const described = await describeRequest(input, init);
    if (described.init?.method === 'POST' && /\/(complete|uncomplete|skip)$/.test(described.url)) sent.push(described);
  }
  return sent;
};

function renderToday() {
  return renderWithProviders(
    <OfflineSyncProvider store={store}>
      <TodayPage now={NOW} />
    </OfflineSyncProvider>,
  );
}

const inSection = (name: string, text: string) =>
  waitFor(() => within(screen.getByRole('region', { name })).getByText(text));

const queuedBadkamer = () => ({
  action: { id: 'o-mine', kind: 'complete' as const },
  profileId: ANNA.id,
  taskName: 'Badkamer',
  queuedAt: '2026-09-16T07:00:00.000Z',
});

describe('offline check-off', () => {
  beforeEach(() => {
    storeProfile(ANNA.id);
    mode = 'online';
    store = memoryStore();
    db = [
      makeOccurrenceV2({ id: 'o-mine', taskId: 't2', taskNameSnapshot: 'Badkamer', date: TODAY, assigneeId: ANNA.id }),
      makeOccurrenceV2({ id: 'o-free', taskId: 't4', taskNameSnapshot: 'Wastafel', date: TODAY, assigneeId: null }),
    ];
  });

  it('keeps a check-off made offline and sends it as the same profile once back online', async () => {
    const fetchMock = stubServer();
    renderToday();
    const check = await screen.findByRole('button', { name: 'Afvinken: Badkamer' });

    mode = 'offline';
    fireEvent.click(check);
    expect(await inSection('Afgerond', 'Badkamer')).toBeInTheDocument();
    expect(await screen.findByText(PENDING_ONE)).toBeInTheDocument();
    expect(screen.queryByText('Dat lukte niet. De wijziging is teruggedraaid.')).not.toBeInTheDocument();
    expect(await store.all()).toMatchObject([{ action: { id: 'o-mine', kind: 'complete' }, profileId: ANNA.id, taskName: 'Badkamer' }]);
    expect(db.find((o) => o.id === 'o-mine')?.status).toBe('open');

    // Someone else picks up the phone before the connection returns.
    storeProfile(BRAM.id);
    mode = 'online';
    await act(async () => {
      window.dispatchEvent(new Event('online'));
    });

    await waitFor(() => expect(db.find((o) => o.id === 'o-mine')?.status).toBe('done'));
    const sent = (await patchCalls(fetchMock)).at(-1)!;
    expect((sent.init!.headers as Record<string, string>)['x-profile-id']).toBe(ANNA.id);
    // Optional fields are left out, not sent as null: the server refuses an explicit null.
    expect(sent.init!.body).toBe('{}');
    expect(db.find((o) => o.id === 'o-mine')?.completedBy).toBe(ANNA.id);
    await waitFor(() => expect(screen.queryByText(PENDING_ONE)).not.toBeInTheDocument());
    expect(await store.all()).toEqual([]);
  });

  it('reports a queued change that the server no longer accepts, and drops it', async () => {
    await store.add(queuedBadkamer());
    mode = 'conflict';
    stubServer();
    renderToday();

    expect(await screen.findByText('"Badkamer" kon niet worden bijgewerkt: de taak is intussen gewijzigd of verwijderd.')).toBeInTheDocument();
    expect(await store.all()).toEqual([]);
    fireEvent.click(screen.getByRole('button', { name: 'Sluiten' }));
    expect(screen.queryByText(/kon niet worden bijgewerkt/)).not.toBeInTheDocument();
  });

  it('keeps queued changes while the server is unavailable', async () => {
    await store.add(queuedBadkamer());
    mode = 'down';
    const fetchMock = stubServer();
    renderToday();

    await waitFor(async () => expect(await patchCalls(fetchMock)).toHaveLength(1));
    expect(await screen.findByText(PENDING_ONE)).toBeInTheDocument();
    expect(await store.all()).toHaveLength(1);
  });

  it('keeps a queued change that got a transient 4xx answer (429) and sends it again', async () => {
    await store.add(queuedBadkamer());
    mode = 'busy';
    const fetchMock = stubServer();
    renderToday();

    await waitFor(async () => expect(await patchCalls(fetchMock)).toHaveLength(1));
    expect(await screen.findByText(PENDING_ONE)).toBeInTheDocument();
    expect(screen.queryByText(/kon niet worden bijgewerkt/)).not.toBeInTheDocument();
    expect(await store.all()).toHaveLength(1);

    mode = 'online';
    await act(async () => {
      window.dispatchEvent(new Event('online'));
    });
    await waitFor(() => expect(db.find((o) => o.id === 'o-mine')?.status).toBe('done'));
    expect(await store.all()).toEqual([]);
  });

  it.each([
    ['a refused request (400)', 'refused'],
    ['an occurrence that is gone (404)', 'conflict'],
  ] as const)('drops a change after %s once and tells the person, without trying again', async (_label, answer) => {
    await store.add(queuedBadkamer());
    mode = answer;
    const fetchMock = stubServer();
    renderToday();

    expect(await screen.findByText(/"Badkamer" kon niet worden bijgewerkt/)).toBeInTheDocument();
    expect(await store.all()).toEqual([]);
    await act(async () => {
      window.dispatchEvent(new Event('online'));
    });
    expect(await patchCalls(fetchMock)).toHaveLength(1);
  });

  it('treats a replay that finds the occurrence already in the wanted state as done, without a conflict', async () => {
    // The first request reached the server but its answer was lost: the occurrence is done already.
    db = db.map((o) => (o.id === 'o-mine' ? { ...o, status: 'done' as const, completedBy: ANNA.id } : o));
    await store.add(queuedBadkamer());
    mode = 'applied';
    const fetchMock = stubServer();
    renderToday();

    await waitFor(async () => expect(await store.all()).toEqual([]));
    expect(await patchCalls(fetchMock)).toHaveLength(1);
    expect(screen.queryByText(/kon niet worden bijgewerkt/)).not.toBeInTheDocument();
  });

  it('reports a replay as a conflict when someone else completed the occurrence meanwhile', async () => {
    db = db.map((o) => (o.id === 'o-mine' ? { ...o, status: 'done' as const, completedBy: BRAM.id } : o));
    await store.add(queuedBadkamer());
    mode = 'applied';
    stubServer();
    renderToday();

    expect(await screen.findByText(/"Badkamer" kon niet worden bijgewerkt/)).toBeInTheDocument();
    expect(await store.all()).toEqual([]);
  });

  it('reads the occurrence fresh for that check, past the service worker cache', async () => {
    db = db.map((o) => (o.id === 'o-mine' ? { ...o, status: 'done' as const, completedBy: ANNA.id } : o));
    await store.add(queuedBadkamer());
    mode = 'applied';
    const fetchMock = stubServer();
    renderToday();

    await waitFor(async () => expect(await store.all()).toEqual([]));
    const checks = [];
    for (const [input, init] of fetchMock.mock.calls) {
      const described = await describeRequest(input, init);
      if (described.url.endsWith('/api/v2/occurrences/o-mine') && (described.init?.method ?? 'GET') === 'GET') checks.push(init?.cache);
    }
    expect(checks).toEqual(['no-store']);
  });

  it('reports a replay that finds the occurrence in another state (409) as a conflict', async () => {
    // The occurrence was skipped by someone else meanwhile, so a queued check-off no longer applies.
    db = db.map((o) => (o.id === 'o-mine' ? { ...o, status: 'skipped' as const } : o));
    await store.add(queuedBadkamer());
    mode = 'applied';
    stubServer();
    renderToday();

    expect(await screen.findByText(/"Badkamer" kon niet worden bijgewerkt/)).toBeInTheDocument();
    expect(await store.all()).toEqual([]);
  });

  it('still needs a connection to claim, and rolls that back', async () => {
    stubServer();
    renderToday();
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: 'all' } });
    const claim = await screen.findByRole('button', { name: 'Wastafel oppakken' });

    mode = 'offline';
    fireEvent.click(claim);
    expect(await screen.findByText('Dat lukte niet. De wijziging is teruggedraaid.')).toBeInTheDocument();
    expect(await inSection('Nog niet opgepakt', 'Wastafel')).toBeInTheDocument();
    expect(await store.all()).toEqual([]);
  });
});
