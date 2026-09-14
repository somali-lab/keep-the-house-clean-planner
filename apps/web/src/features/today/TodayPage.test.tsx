import type { OccurrenceView } from '@huishoudplanner/shared';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeOccurrence, makeRoom, makeSettings, makeTask, renderWithProviders } from '../../test/render.tsx';
import { applyOptimistic } from './api.ts';
import { TodayPage } from './TodayPage.tsx';

const NOW = new Date('2026-09-16T08:00:00Z'); // Wednesday 10:00 Amsterdam
const TODAY = '2026-09-16';

let db: OccurrenceView[];
let failNext = false;

/** Tiny in-memory server so refetches after a mutation reflect the change. */
function setup() {
  storeProfile(ANNA._id);
  db = [
    makeOccurrence({ _id: 'o-other', taskId: 't1', taskNameSnapshot: 'Stofzuigen', date: TODAY, assigneeId: BRAM._id }),
    makeOccurrence({ _id: 'o-mine', taskId: 't2', taskNameSnapshot: 'Badkamer', date: TODAY, assigneeId: ANNA._id, durationMinutesSnapshot: 30 }),
    makeOccurrence({ _id: 'o-late', taskId: 't3', taskNameSnapshot: 'Ramen', date: '2026-09-14', plannedDate: '2026-09-14', assigneeId: ANNA._id, isOverdue: true }),
    makeOccurrence({ _id: 'o-free', taskId: 't4', taskNameSnapshot: 'Wastafel', date: TODAY, assigneeId: null }),
  ];
  const patch = (id: string) => (init: RequestInit) => {
    if (failNext) {
      failNext = false;
      throw new Error('boom');
    }
    const body = JSON.parse(String(init.body)) as { action: 'complete' | 'uncomplete' | 'skip'; completedBy?: string; reason?: string };
    const current = db.find((o) => o._id === id)!;
    const next = applyOptimistic(current, { id, kind: body.action, completedBy: body.completedBy, reason: body.reason } as never, {
      profileId: ANNA._id,
      todayKey: TODAY,
      now: NOW,
    });
    db = db.map((o) => (o._id === id ? next : o));
    return next;
  };
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': makeSettings(),
    '/api/rooms': [makeRoom({ _id: 'r1', name: 'Badkamer-ruimte' })],
    '/api/tasks': [makeTask({ _id: 't2', name: 'Badkamer', roomId: 'r1' })],
    '/api/occurrences': () => db,
    ...Object.fromEntries(db.map((o) => [`PATCH /api/occurrences/${o._id}`, patch(o._id)])),
    'POST /api/occurrences/o-free/claim': () => {
      db = db.map((o) => (o._id === 'o-free' ? { ...o, assigneeId: ANNA._id } : o));
      return db.find((o) => o._id === 'o-free');
    },
  });
}

const sectionTitles = () => screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent);
const section = (name: string) => screen.getByRole('region', { name });
/** Optimistic updates land one tick after the click (onMutate awaits cancelQueries). */
const inSection = async (name: string, text: string) =>
  waitFor(() => within(section(name)).getByText(text));

describe('TodayPage', () => {
  beforeEach(() => {
    failNext = false;
  });

  it('orders sections: mine, unclaimed, the other person, overdue', async () => {
    const fetchMock = setup();
    renderWithProviders(<TodayPage now={NOW} />);
    await screen.findByRole('heading', { name: 'Mijn taken vandaag' });

    expect(sectionTitles()).toEqual(['Mijn taken vandaag', 'Nog niet opgepakt', 'Van de ander', 'Achterstallig']);
    expect(within(section('Mijn taken vandaag')).getByText('Badkamer')).toBeInTheDocument();
    expect(within(section('Mijn taken vandaag')).getByText(/Badkamer-ruimte · 30 min · Anna/)).toBeInTheDocument();
    expect(within(section('Nog niet opgepakt')).getByText('Wastafel')).toBeInTheDocument();
    expect(within(section('Van de ander')).getByText('Stofzuigen')).toBeInTheDocument();
    const late = within(section('Achterstallig')).getByText('Ramen').closest('li')!;
    expect(late).toHaveTextContent('Achterstallig — gepland op ma 14-09');

    const url = String(fetchMock.mock.calls.find(([u]) => String(u).startsWith('/api/occurrences'))![0]);
    expect(url).toBe('/api/occurrences?from=2026-07-22&to=2026-09-16');
  });

  it('checks off with one tap, and undo restores the previous status', async () => {
    const fetchMock = setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Badkamer' }));

    // moved to finished, snackbar offers undo
    expect(await inSection('Afgerond vandaag', 'Badkamer')).toBeInTheDocument();
    const snackbar = screen.getByRole('status');
    expect(snackbar).toHaveTextContent('"Badkamer" afgevinkt.');
    await waitFor(() => expect(db.find((o) => o._id === 'o-mine')?.status).toBe('done'));

    fireEvent.click(within(snackbar).getByRole('button', { name: 'Ongedaan maken' }));
    expect(await inSection('Mijn taken vandaag', 'Badkamer')).toBeInTheDocument();
    await waitFor(() => expect(db.find((o) => o._id === 'o-mine')?.status).toBe('open'));
    expect(screen.queryByRole('region', { name: 'Afgerond vandaag' })).not.toBeInTheDocument();
    const bodies = fetchMock.mock.calls
      .filter(([u]) => u === '/api/occurrences/o-mine')
      .map(([, init]) => JSON.parse(String((init as RequestInit).body)));
    expect(bodies).toEqual([{ action: 'complete' }, { action: 'uncomplete' }]);
  });

  it("credits the task's assignee when another profile checks it off", async () => {
    setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Stofzuigen' }));

    await waitFor(() => expect(db.find((o) => o._id === 'o-other')?.completedBy).toBe(BRAM._id));
    expect(await inSection('Afgerond vandaag', 'Gedaan door Bram de Vries')).toBeInTheDocument();
  });

  it('undo after skip → done restores skipped, also later via the item', async () => {
    setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Meer voor Badkamer' }));
    fireEvent.click(screen.getByRole('button', { name: 'Overslaan' }));
    fireEvent.change(screen.getByLabelText('Reden (optioneel)'), { target: { value: 'geen tijd' } });
    fireEvent.click(screen.getByRole('button', { name: 'Overslaan bevestigen' }));
    await waitFor(() => expect(db.find((o) => o._id === 'o-mine')?.status).toBe('skipped'));
    expect(await inSection('Afgerond vandaag', 'Overgeslagen: geen tijd')).toBeInTheDocument();

    // the skipped item gets completed elsewhere; any refetch picks that up
    db = db.map((o) => (o._id === 'o-mine' ? applyOptimistic(o, { id: o._id, kind: 'complete' }, { profileId: ANNA._id, todayKey: TODAY, now: NOW }) : o));
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Afvinken: Ramen' }));
    });
    const doneRow = (await inSection('Afgerond vandaag', 'Gedaan door Anna')).closest('li')!;
    fireEvent.click(within(doneRow).getByRole('button', { name: 'Badkamer ongedaan maken' }));
    await waitFor(() => expect(db.find((o) => o._id === 'o-mine')?.status).toBe('skipped'));
    expect(await inSection('Afgerond vandaag', 'Overgeslagen: geen tijd')).toBeInTheDocument();
  });

  it('attributes a check-off to another profile via the menu', async () => {
    const fetchMock = setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Meer voor Badkamer' }));
    fireEvent.change(screen.getByLabelText('Afgevinkt door'), { target: { value: BRAM._id } });
    fireEvent.click(screen.getByRole('button', { name: 'Afvinken namens' }));
    await waitFor(() => {
      const call = fetchMock.mock.calls.find(([u]) => u === '/api/occurrences/o-mine');
      expect(JSON.parse(String((call![1] as RequestInit).body))).toEqual({ action: 'complete', completedBy: BRAM._id });
    });
    expect(await inSection('Afgerond vandaag', 'Gedaan door Bram de Vries')).toBeInTheDocument();
  });

  it('claims an unclaimed item', async () => {
    setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Wastafel oppakken' }));
    expect(await inSection('Mijn taken vandaag', 'Wastafel')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Nog niet opgepakt' })).not.toBeInTheDocument();
  });

  it('rolls back an optimistic check-off when the server fails', async () => {
    setup();
    failNext = true;
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Badkamer' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Dat lukte niet. De wijziging is teruggedraaid.');
    expect(await inSection('Mijn taken vandaag', 'Badkamer')).toBeInTheDocument();
    expect(db.find((o) => o._id === 'o-mine')?.status).toBe('open');
  });

  it('uses large tap targets for checking off', async () => {
    setup();
    renderWithProviders(<TodayPage now={NOW} />);
    const button = await screen.findByRole('button', { name: 'Afvinken: Badkamer' });
    expect(button).toHaveClass('check-button');
  });
});
