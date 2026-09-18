import type { AuditEntry } from '@huishoudplanner/shared';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeRoom, makeSettings, makeTask, renderWithProviders } from '../../test/render.tsx';
import { SYSTEM_ACTOR_ID } from './describe.ts';
import { HistoryPage } from './HistoryPage.tsx';

const entry = (overrides: Partial<AuditEntry> & Pick<AuditEntry, '_id' | 'at'>): AuditEntry => ({
  actorId: ANNA._id,
  entity: 'task',
  entityId: 't1',
  action: 'update',
  before: {},
  after: {},
  source: 'ui',
  ...overrides,
});

const PAGE_1: AuditEntry[] = [
  entry({ _id: 'e4', at: '2026-09-16T09:30:00.000Z', entity: 'cyclePlan', entityId: 'p1', action: 'ai-apply', after: { active: true }, source: 'ai', meta: { proposalId: 'abc' } }),
  entry({ _id: 'e3', at: '2026-09-16T09:00:00.000Z', entity: 'occurrence', entityId: 'o1', action: 'complete', before: { status: 'open' }, after: { status: 'done', completedBy: BRAM._id }, meta: { completedBy: BRAM._id, wasAssignee: false } }),
  entry({ _id: 'e2', at: '2026-09-16T08:30:00.000Z', before: { durationMinutes: 30 }, after: { durationMinutes: 45 } }),
  entry({ _id: 'e1', at: '2026-09-16T08:00:00.000Z', actorId: SYSTEM_ACTOR_ID, source: 'system', entity: 'occurrence', entityId: 'o1', action: 'create', after: { taskNameSnapshot: 'Badkamer schoonmaken', status: 'open' }, meta: { runId: 'r' } }),
];
const PAGE_2: AuditEntry[] = [
  entry({ _id: 'e0', at: '2026-09-15T08:00:00.000Z', action: 'create', after: { name: 'Badkamer schoonmaken' } }),
];

function setup(audit: (init: RequestInit | undefined, url: string) => unknown, extra: Record<string, unknown> = {}) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/tasks': [makeTask({ _id: 't1', name: 'Badkamer schoonmaken', roomId: 'r1' })],
    '/api/rooms': [makeRoom({ _id: 'r1', name: 'Badkamer' })],
    '/api/settings': makeSettings(),
    '/api/cycle-plans': [{ _id: 'p1', name: 'Zomerplan' }],
    '/api/audit': audit,
    ...extra,
  });
}

const auditUrls = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.map(([u]) => String(u)).filter((u) => u.startsWith('/api/audit'));

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
    expect(auditUrls(fetchMock)[0]).toBe('/api/audit?entity=task&entityId=t1&limit=50');
  });

  it('filters by actor, entity type and date range', async () => {
    const fetchMock = setup(() => ({ items: PAGE_1, nextCursor: null }));
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    await screen.findAllByRole('listitem');

    fireEvent.change(screen.getByLabelText('Wie'), { target: { value: BRAM._id } });
    await waitFor(() => expect(auditUrls(fetchMock).at(-1)).toContain(`actorId=${BRAM._id}`));

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
  });

  it('loads more entries with the cursor', async () => {
    const fetchMock = setup((_init, url) =>
      url.includes('cursor=c1') ? { items: PAGE_2, nextCursor: null } : { items: PAGE_1, nextCursor: 'c1' },
    );
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    expect(await screen.findAllByRole('listitem')).toHaveLength(4);

    fireEvent.click(screen.getByRole('button', { name: 'Meer laden' }));
    await waitFor(() => expect(screen.getAllByRole('listitem')).toHaveLength(5));
    expect(auditUrls(fetchMock).at(-1)).toContain('cursor=c1');
    expect(screen.queryByRole('button', { name: 'Meer laden' })).not.toBeInTheDocument();
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
      { 'DELETE /api/audit': () => { cleared = true; return { deleted: PAGE_1.length }; } },
    );
    renderWithProviders(<HistoryPage />, { route: '/manage/history' });
    await screen.findAllByRole('listitem');

    fireEvent.click(screen.getByRole('button', { name: 'Geschiedenis wissen' }));
    expect(screen.getByRole('heading', { name: 'Alle geschiedenis wissen?' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Definitief wissen' }));

    expect(await screen.findByText('Geen wijzigingen gevonden.')).toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([url, init]) => url === '/api/audit' && (init as RequestInit)?.method === 'DELETE')).toBe(true);
  });
});
