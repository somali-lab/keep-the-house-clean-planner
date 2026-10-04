import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { AuditEntry } from './auditModel.ts';
import { ANNA, BRAM, makeUser, mockApi, problem, requestsTo, storeProfile, page, v2Basics } from '../../test/fixtures.ts';
import { makeRoomV2, makeSettings, makeTaskV2, renderWithProviders } from '../../test/render.tsx';
import { SYSTEM_ACTOR_ID } from './describe.ts';
import { HistoryPage } from './HistoryPage.tsx';

const entry = (overrides: Partial<AuditEntry> & Pick<AuditEntry, 'id' | 'at'>): AuditEntry => ({
  actorId: ANNA.id,
  entity: 'task',
  entityId: 't1',
  action: 'update',
  before: {},
  after: {},
  source: 'ui',
  meta: null,
  ...overrides,
});

const PAGE_1: AuditEntry[] = [
  entry({ id: 'e4', at: '2026-09-16T09:30:00.000Z', entity: 'cyclePlan', entityId: 'p1', action: 'ai-apply', after: { active: true }, source: 'ai', meta: { proposalId: 'abc' } }),
  entry({ id: 'e3', at: '2026-09-16T09:00:00.000Z', entity: 'occurrence', entityId: 'o1', action: 'complete', before: { status: 'open' }, after: { status: 'done', completedBy: BRAM.id }, meta: { completedBy: BRAM.id, wasAssignee: false } }),
  entry({ id: 'e2', at: '2026-09-16T08:30:00.000Z', before: { durationMinutes: 30 }, after: { durationMinutes: 45 } }),
  entry({ id: 'e1', at: '2026-09-16T08:00:00.000Z', actorId: SYSTEM_ACTOR_ID, source: 'system', entity: 'occurrence', entityId: 'o1', action: 'create', after: { taskNameSnapshot: 'Badkamer schoonmaken', status: 'open' }, meta: { runId: 'r' } }),
];
const PAGE_2: AuditEntry[] = [
  entry({ id: 'e0', at: '2026-09-15T08:00:00.000Z', action: 'create', after: { name: 'Badkamer schoonmaken' } }),
];

const PLAN = {
  id: 'p1',
  name: 'Zomerplan',
  active: true,
  slots: [],
  weekThemes: ['', '', '', ''],
  draft: false,
  source: 'manual',
  proposalId: null,
  rationale: null,
  discarded: false,
  createdAt: '2026-09-14T08:00:00.000Z',
  updatedAt: '2026-09-14T08:00:00.000Z',
  version: 1,
};

function setup(audit: (init: RequestInit | undefined, url: string) => unknown, extra: Record<string, unknown> = {}) {
  storeProfile(ANNA.id);
  return mockApi({
    '/api/v2/users': page([ANNA, BRAM]),
    '/api/v2/tasks': page([makeTaskV2({ id: 't1', name: 'Badkamer schoonmaken', roomId: 'r1' })]),
    '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Badkamer' })]),
    '/api/v2/settings': makeSettings(),
    '/api/v2/cycle-plans': page([PLAN]),
    ...v2Basics(),
    '/api/v2/audit': audit,
    ...extra,
  });
}

const auditUrls = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.map(([u]) => String(u)).filter((u) => u.startsWith('/api/v2/audit'));

describe('HistoryPage', () => {
  it('shows readable before/after text, actor names and the AI label', async () => {
    setup(() => ({ items: PAGE_1, nextCursor: null }));
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });

    const items = await screen.findAllByRole('listitem');
    await waitFor(() => expect(items[2]).toHaveTextContent('Anna wijzigde duur van Badkamer schoonmaken: 30 → 45 min'));
    expect(items[1]).toHaveTextContent('Anna vinkte Badkamer schoonmaken af, gedaan door Bram de Vries');
    expect(items[3]).toHaveTextContent('Systeem maakte taak op een dag Badkamer schoonmaken aan');

    expect(items[0]).toHaveTextContent('Anna paste een AI-voorstel toe op Zomerplan');
    expect(within(items[0]!).getByText('via AI-voorstel')).toBeInTheDocument();
    expect(items.slice(1).some((li) => li.textContent?.includes('via AI-voorstel'))).toBe(false);
  });

  it('works as a history panel for one entity', async () => {
    const fetchMock = setup(() => ({ items: [PAGE_1[2]], nextCursor: null }));
    renderWithProviders(<HistoryPage />, { route: '/manage/history?entity=task&entityId=t1' });

    expect(await screen.findByRole('heading', { name: 'Geschiedenis van Badkamer schoonmaken' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Alle geschiedenis' })).toHaveAttribute('href', '/manage/history');
    expect(screen.queryByRole('search')).not.toBeInTheDocument();
    expect(auditUrls(fetchMock)[0]).toBe('/api/v2/audit?entity=task&entityId=t1&limit=50');
  });

  it('filters by actor, entity type and date range', async () => {
    const fetchMock = setup(() => ({ items: PAGE_1, nextCursor: null }));
    renderWithProviders(<HistoryPage />, { route: '/manage/history', headerReset: true });
    await screen.findAllByRole('listitem');

    fireEvent.change(screen.getByLabelText('Wie'), { target: { value: BRAM.id } });
    await waitFor(() => expect(auditUrls(fetchMock).at(-1)).toContain(`actorId=${BRAM.id}`));

    fireEvent.change(screen.getByLabelText('Soort'), { target: { value: 'occurrence' } });
    await waitFor(() => expect(auditUrls(fetchMock).at(-1)).toContain('entity=occurrence'));

    fireEvent.change(screen.getByLabelText('Vanaf'), { target: { value: '2026-09-01' } });
    fireEvent.change(screen.getByLabelText('Tot en met'), { target: { value: '2026-09-30' } });
    await waitFor(() => {
      const url = new URL(auditUrls(fetchMock).at(-1)!, 'http://x');
      expect(new Date(url.searchParams.get('from')!).getTime()).toBe(new Date('2026-09-01T00:00:00').getTime());
      expect(new Date(url.searchParams.get('to')!).getTime()).toBe(new Date('2026-09-30T23:59:59.999').getTime());
    });

    expect(screen.getByLabelText('Wie')).toHaveDisplayValue('Bram de Vries');
    expect(within(screen.getByLabelText('Wie')).getByRole('option', { name: 'Systeem' })).toHaveValue(SYSTEM_ACTOR_ID);
    fireEvent.click(screen.getByRole('button', { name: 'Filters van dit scherm resetten' }));
    await waitFor(() => expect(auditUrls(fetchMock).at(-1)).toBe('/api/v2/audit?limit=50'));
    expect(screen.getByLabelText('Wie')).toHaveValue('');
  });

  it('loads more entries with the cursor, one page at a time, and says how much is loaded', async () => {
    const fetchMock = setup((_init, url) =>
      url.includes('cursor=c1') ? { items: PAGE_2, nextCursor: null } : { items: PAGE_1, nextCursor: 'c1' },
    );
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    expect(await screen.findAllByRole('listitem')).toHaveLength(4);
    expect(auditUrls(fetchMock)).toEqual(['/api/v2/audit?limit=50']);
    expect(screen.getByText('4 getoond')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Meer laden' }));
    await waitFor(() => expect(screen.getAllByRole('listitem')).toHaveLength(5));
    expect(auditUrls(fetchMock)).toEqual(['/api/v2/audit?limit=50', '/api/v2/audit?limit=50&cursor=c1']);
    expect(screen.queryByRole('button', { name: 'Meer laden' })).not.toBeInTheDocument();
    expect(screen.getByText('Alles geladen (5)').closest('[aria-live]')).toHaveAttribute('aria-live', 'polite');
  });

  it('keeps the list when the next page fails and retries the same cursor', async () => {
    let failNext = true;
    const fetchMock = setup((_init, url) => {
      if (!url.includes('cursor=c1')) return { items: PAGE_1, nextCursor: 'c1' };
      return failNext ? problem(500, 'boom') : { items: PAGE_2, nextCursor: null };
    });
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    await screen.findAllByRole('listitem');
    fireEvent.click(screen.getByRole('button', { name: 'Meer laden' }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Meer laden is mislukt.');
    expect(screen.getAllByRole('listitem')).toHaveLength(4);

    failNext = false;
    fireEvent.click(within(alert).getByRole('button', { name: 'Opnieuw proberen' }));
    await waitFor(() => expect(screen.getAllByRole('listitem')).toHaveLength(5));
    expect(auditUrls(fetchMock)).toEqual(['/api/v2/audit?limit=50', '/api/v2/audit?limit=50&cursor=c1', '/api/v2/audit?limit=50&cursor=c1']);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('starts over on the first page when a filter changes', async () => {
    const fetchMock = setup((_init, url) =>
      url.includes('cursor=c1') ? { items: PAGE_2, nextCursor: null } : { items: PAGE_1, nextCursor: 'c1' },
    );
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    await screen.findAllByRole('listitem');
    fireEvent.click(screen.getByRole('button', { name: 'Meer laden' }));
    await waitFor(() => expect(screen.getAllByRole('listitem')).toHaveLength(5));

    fireEvent.change(screen.getByLabelText('Wie'), { target: { value: BRAM.id } });
    await waitFor(() => expect(auditUrls(fetchMock).at(-1)).toBe(`/api/v2/audit?actorId=${BRAM.id}&limit=50`));
    await waitFor(() => expect(screen.getAllByRole('listitem')).toHaveLength(4));
    expect(screen.getByRole('button', { name: 'Meer laden' })).toBeInTheDocument();
  });

  it('says when there is nothing to show', async () => {
    setup(() => ({ items: [], nextCursor: null }));
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    expect(await screen.findByText('Geen wijzigingen gevonden.')).toBeInTheDocument();
  });

  it('clears all history after explicit confirmation', async () => {
    let cleared = false;
    const fetchMock = setup(
      () => ({ items: cleared ? [] : PAGE_1, nextCursor: null }),
      { 'DELETE /api/v2/audit': () => { cleared = true; return { deleted: PAGE_1.length }; } },
    );
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    await screen.findAllByRole('listitem');

    fireEvent.click(screen.getByRole('button', { name: 'Geschiedenis wissen' }));
    expect(screen.getByRole('heading', { name: 'Alle geschiedenis wissen?' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Definitief wissen' }));

    expect(await screen.findByText('Geen wijzigingen gevonden.')).toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([url, init]) => url === '/api/v2/audit' && (init as RequestInit)?.method === 'DELETE')).toBe(true);
  });

  it('clears without If-Match, as the administrator, and tells when the server refuses', async () => {
    const fetchMock = setup(() => ({ items: PAGE_1, nextCursor: null }), {
      'DELETE /api/v2/audit': () => problem(403, 'forbidden', 'Administrators only.'),
    });
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    await screen.findAllByRole('listitem');

    fireEvent.click(screen.getByRole('button', { name: 'Geschiedenis wissen' }));
    fireEvent.click(screen.getByRole('button', { name: 'Definitief wissen' }));
    expect(await screen.findByText('De geschiedenis kon niet worden gewist.')).toBeInTheDocument();
    const [request] = requestsTo(fetchMock, 'DELETE', '/api/v2/audit');
    expect(request?.headers['x-profile-id']).toBe(ANNA.id);
    expect(request?.headers['if-match']).toBeUndefined();
    expect(screen.getAllByRole('listitem', { hidden: true })).toHaveLength(4);
    // The failure of this try is gone when the dialog is opened again.
    fireEvent.click(screen.getByRole('button', { name: 'Annuleren' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Geschiedenis wissen' }));
    expect(screen.queryByText('De geschiedenis kon niet worden gewist.')).not.toBeInTheDocument();
  });

  it('offers the clear button to administrators only', async () => {
    const member = makeUser({ id: 'c00000000000000000000003', name: 'Carla', role: 'member' });
    setup(() => ({ items: PAGE_1, nextCursor: null }), { '/api/v2/users': page([ANNA, BRAM, member]) });
    storeProfile(member.id);
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    await screen.findAllByRole('listitem');
    expect(screen.queryByRole('button', { name: 'Geschiedenis wissen' })).not.toBeInTheDocument();
  });

  it('labels a week bonus of a reconciliation with the ISO week of the server calendar', async () => {
    const recompute = entry({
      id: 'e9',
      at: '2026-09-29T01:00:00.000Z',
      actorId: SYSTEM_ACTOR_ID,
      source: 'system',
      entity: 'points',
      entityId: 'ledger',
      action: 'recompute',
      meta: {
        bonusChanges: [{ key: `bonus_week_done:${BRAM.id}:2026-09-28`, personId: BRAM.id, amount: 5, change: 'created' }],
        bonusChangesTotal: 1,
      },
    });
    const fetchMock = setup(() => ({ items: [recompute], nextCursor: null }));
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });

    expect(await screen.findByText('Bram de Vries kreeg 5 punten: Weekbonus: alles gedaan, week 40')).toBeInTheDocument();
    const calendarCalls = fetchMock.mock.calls.map(([url]) => String(url)).filter((url) => url.startsWith('/api/v2/calendar'));
    expect(calendarCalls).toEqual(['/api/v2/calendar?from=2026-09-28&to=2026-09-28']);
  });

  it('asks for no calendar when no reconciliation names a week bonus', async () => {
    const fetchMock = setup(() => ({ items: PAGE_1, nextCursor: null }));
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    await screen.findAllByRole('listitem');
    expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith('/api/v2/calendar'))).toBe(false);
  });

  it('says so when the history cannot be read', async () => {
    setup(() => problem(500, 'internal_error'));
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    expect(await screen.findByRole('alert')).toHaveTextContent('Er ging iets mis');
  });
});
