import type { OccurrenceView } from '@huishoudplanner/shared';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { applyOptimistic } from '../features/today/api.ts';
import { TodayPage } from '../features/today/TodayPage.tsx';
import { ANNA, BRAM, storeProfile } from '../test/fixtures.ts';
import { makeOccurrence, makeSettings, renderWithProviders } from '../test/render.tsx';
import { OfflineSyncProvider } from './OfflineSyncProvider.tsx';
import { memoryStore, type QueueStore } from './queue.ts';

const NOW = new Date('2026-09-16T08:00:00Z');
const TODAY = '2026-09-16';
const PENDING_ONE = '1 wijziging is offline bewaard en wordt verstuurd zodra er weer verbinding is.';

type Mode = 'online' | 'offline' | 'conflict' | 'down';

let db: OccurrenceView[];
let mode: Mode;
let store: QueueStore;

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

/** A small server that can lose its connection, answer 404, or fail with 500. */
function stubServer() {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    if (mode === 'offline') throw new TypeError('Failed to fetch');

    if (method === 'GET') {
      const path = url.split('?')[0]!;
      const routes: Record<string, unknown> = {
        '/api/users': [ANNA, BRAM],
        '/api/settings': makeSettings(),
        '/api/rooms': [],
        '/api/tasks': [],
        '/api/occurrences': db,
      };
      return path in routes ? json(routes[path]) : json({ code: 'not_found' }, 404);
    }

    if (mode === 'conflict') return json({ code: 'not_found' }, 404);
    if (mode === 'down') return json({ code: 'internal_error' }, 500);
    const match = /^\/api\/occurrences\/([^/]+)$/.exec(url);
    if (method === 'PATCH' && match) {
      const body = JSON.parse(String(init!.body)) as { action: 'complete' | 'uncomplete' | 'skip' };
      const current = db.find((o) => o._id === match[1])!;
      const profileId = (init!.headers as Record<string, string>)['X-Profile-Id']!;
      const next = applyOptimistic(current, { id: current._id, kind: body.action }, { profileId, todayKey: TODAY, now: NOW });
      db = db.map((o) => (o._id === next._id ? next : o));
      return json(next);
    }
    return json({ code: 'not_found' }, 404);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const patchCalls = (fetchMock: ReturnType<typeof stubServer>) =>
  fetchMock.mock.calls.filter(([, init]) => init?.method === 'PATCH');

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
  profileId: ANNA._id,
  taskName: 'Badkamer',
  queuedAt: '2026-09-16T07:00:00.000Z',
});

describe('offline check-off', () => {
  beforeEach(() => {
    storeProfile(ANNA._id);
    mode = 'online';
    store = memoryStore();
    db = [
      makeOccurrence({ _id: 'o-mine', taskId: 't2', taskNameSnapshot: 'Badkamer', date: TODAY, assigneeId: ANNA._id }),
      makeOccurrence({ _id: 'o-free', taskId: 't4', taskNameSnapshot: 'Wastafel', date: TODAY, assigneeId: null }),
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
    expect(await store.all()).toMatchObject([{ action: { id: 'o-mine', kind: 'complete' }, profileId: ANNA._id, taskName: 'Badkamer' }]);
    expect(db.find((o) => o._id === 'o-mine')?.status).toBe('open');

    // Someone else picks up the phone before the connection returns.
    storeProfile(BRAM._id);
    mode = 'online';
    await act(async () => {
      window.dispatchEvent(new Event('online'));
    });

    await waitFor(() => expect(db.find((o) => o._id === 'o-mine')?.status).toBe('done'));
    const sent = patchCalls(fetchMock).at(-1)!;
    expect((sent[1]!.headers as Record<string, string>)['X-Profile-Id']).toBe(ANNA._id);
    expect(db.find((o) => o._id === 'o-mine')?.completedBy).toBe(ANNA._id);
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

    await waitFor(() => expect(patchCalls(fetchMock)).toHaveLength(1));
    expect(await screen.findByText(PENDING_ONE)).toBeInTheDocument();
    expect(await store.all()).toHaveLength(1);
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
