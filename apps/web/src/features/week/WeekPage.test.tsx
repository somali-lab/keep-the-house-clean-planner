import type { ApiWarning, OccurrenceView } from '@huishoudplanner/shared';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { createElement, type ComponentProps } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeOccurrence, makeRoom, makeSettings, makeTask, renderWithProviders } from '../../test/render.tsx';
import { WeekPage } from './WeekPage.tsx';
import { groupByDay, movedTo, overviewDays, shortDay, weekDays, weekRangeLabel } from './weekModel.ts';

const dnd = vi.hoisted(() => ({ onDragEnd: undefined as undefined | ((event: unknown) => void) }));
vi.mock('@dnd-kit/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@dnd-kit/core')>();
  return {
    ...actual,
    DndContext: (props: ComponentProps<typeof actual.DndContext>) => {
      dnd.onDragEnd = props.onDragEnd as (event: unknown) => void;
      return createElement(actual.DndContext, props);
    },
  };
});

const NOW = new Date('2026-09-16T08:00:00Z'); // Wednesday; week 14–20 Sep

let db: OccurrenceView[];
let nextWarnings: ApiWarning[] = [];

function setup() {
  storeProfile(ANNA._id);
  db = [
    makeOccurrence({ _id: 'o1', taskNameSnapshot: 'Badkamer', date: '2026-09-15', assigneeId: ANNA._id }),
    makeOccurrence({ _id: 'o2', taskNameSnapshot: 'Stofzuigen', date: '2026-09-17', plannedDate: '2026-09-16', movedFrom: '2026-09-16', assigneeId: BRAM._id }),
    makeOccurrence({ _id: 'o3', taskNameSnapshot: 'Afwas', date: '2026-09-15', status: 'done', assigneeId: ANNA._id }),
  ];
  const update = (id: string) => (init: RequestInit) => {
    const body = JSON.parse(String(init.body)) as { action: 'reschedule' | 'complete' | 'uncomplete'; date?: string };
    const current = db.find((o) => o._id === id)!;
    const updated =
      body.action === 'reschedule'
        ? movedTo(current, body.date!)
        : body.action === 'complete'
          ? { ...current, status: 'done' as const, completedBy: ANNA._id, completedAt: NOW.toISOString(), statusBeforeCompletion: 'open' as const }
          : { ...current, status: 'open' as const, completedBy: null, completedAt: null, statusBeforeCompletion: null };
    db = db.map((o) => (o._id === id ? updated : o));
    return { ...updated, warnings: nextWarnings };
  };
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': makeSettings(),
    '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
    '/api/tasks': [makeTask({ _id: 't1', name: 'Huishoudtaak', roomId: 'r1' })],
    '/api/occurrences': () => db,
    'PATCH /api/occurrences/o1': update('o1'),
    'PATCH /api/occurrences/o2': update('o2'),
  });
}

const patchBodies = (fetchMock: ReturnType<typeof mockApi>, id: string) =>
  fetchMock.mock.calls
    .filter(([u, init]) => u === `/api/occurrences/${id}` && (init as RequestInit | undefined)?.method === 'PATCH')
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)));

describe('weekModel', () => {
  it('lists Monday to Sunday and formats days', () => {
    expect(weekDays('2026-09-16')).toEqual(['2026-09-14', '2026-09-15', '2026-09-16', '2026-09-17', '2026-09-18', '2026-09-19', '2026-09-20']);
    expect(overviewDays('2026-09-16')).toEqual([
      '2026-09-13', '2026-09-14', '2026-09-15', '2026-09-16', '2026-09-17', '2026-09-18',
      '2026-09-19', '2026-09-20', '2026-09-21', '2026-09-22', '2026-09-23', '2026-09-24',
    ]);
    expect(shortDay('2026-09-08')).toBe('di 8 sep');
    expect(weekRangeLabel('2026-09-14', '2026-09-20')).toBe('14 – 20 sep 2026');
    expect(weekRangeLabel('2026-09-28', '2026-10-04')).toBe('28 sep – 4 okt 2026');
  });

  it('groups by day and derives movedFrom from plannedDate', () => {
    const occ = makeOccurrence({ _id: 'x', date: '2026-09-15', plannedDate: '2026-09-15' });
    expect(movedTo(occ, '2026-09-18')).toMatchObject({ date: '2026-09-18', movedFrom: '2026-09-15' });
    expect(movedTo(movedTo(occ, '2026-09-18'), '2026-09-15').movedFrom).toBeNull();
    expect(groupByDay([occ], ['2026-09-15', '2026-09-16']).map((d) => d.items.length)).toEqual([1, 0]);
  });
});

describe('WeekPage', () => {
  beforeEach(() => {
    nextWarnings = [];
    dnd.onDragEnd = undefined;
  });

  it('shows three collapsible past days, today and eight future days with distinct borders', async () => {
    setup();
    renderWithProviders(<WeekPage now={NOW} />);
    expect(await screen.findByRole('button', { name: /Afgelopen 3 dagen/ })).toHaveAttribute('aria-expanded', 'false');
    fireEvent.change(screen.getByLabelText('Filter op persoon'), { target: { value: 'all' } });
    expect(screen.queryByRole('heading', { name: /^zondag 13 sep/ })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Afgelopen 3 dagen/ }));
    await screen.findByRole('heading', { name: /^zondag 13 sep/ });
    expect(screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent)).toEqual([
      'zondag 13 sep',
      'maandag 14 sep',
      'dinsdag 15 sep',
      'woensdag 16 sep Vandaag',
      'donderdag 17 sep',
      'vrijdag 18 sep',
      'zaterdag 19 sep',
      'zondag 20 sep',
      'maandag 21 sep',
      'dinsdag 22 sep',
      'woensdag 23 sep',
      'donderdag 24 sep',
    ]);
    expect(screen.getByTestId('day:2026-09-13')).toHaveAttribute('data-period', 'past');
    expect(screen.getByTestId('day:2026-09-16')).toHaveAttribute('data-period', 'today');
    expect(screen.getByTestId('day:2026-09-24')).toHaveAttribute('data-period', 'future');
    expect(within(screen.getByTestId('day:2026-09-17')).getByText('verplaatst van wo 16 sep')).toBeInTheDocument();
    expect(within(screen.getByTestId('day:2026-09-17')).getByLabelText('15 min')).toBeInTheDocument();
    expect(within(screen.getByTestId('day:2026-09-17')).getByText('Woonkamer')).toBeInTheDocument();
    expect(within(screen.getByTestId('day:2026-09-17')).getByText('BV')).toBeInTheDocument();
    expect(within(screen.getByTestId('day:2026-09-14')).getByText('Niets gepland.')).toBeInTheDocument();
  });

  it('browses between periods and can return to the days around today', async () => {
    setup();
    renderWithProviders(<WeekPage now={NOW} />);
    await screen.findByText('13 – 24 sep 2026');

    fireEvent.click(screen.getByRole('button', { name: 'Volgende periode' }));
    expect(await screen.findByText('20 sep – 1 okt 2026')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Volgende periode' }));
    expect(await screen.findByText('27 sep – 8 okt 2026')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Rond vandaag' }));
    expect(await screen.findByText('13 – 24 sep 2026')).toBeInTheDocument();
  });

  it('keeps the overview compact without separate move buttons', async () => {
    setup();
    renderWithProviders(<WeekPage now={NOW} />);
    const pastDays = await screen.findByRole('button', { name: /Afgelopen 3 dagen/ });
    expect(screen.getByTestId('week-summary')).toHaveClass('min-h-12', 'py-2');
    expect(pastDays).toHaveClass('min-h-11', 'py-2');
    expect(pastDays).not.toHaveClass('mb-4');
    fireEvent.click(pastDays);
    await screen.findByText('Badkamer');
    expect(screen.queryByRole('button', { name: /Verplaats/ })).not.toBeInTheDocument();
  });

  it('filters the overview by person and by unassigned tasks', async () => {
    setup();
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: /Afgelopen 3 dagen/ }));

    const filter = screen.getByLabelText('Filter op persoon');
    expect(filter).toHaveValue(ANNA._id);
    expect(screen.queryByText('Stofzuigen')).not.toBeInTheDocument();
    fireEvent.change(filter, { target: { value: BRAM._id } });
    expect(await screen.findByText('Stofzuigen')).toBeInTheDocument();
    expect(screen.queryByText('Badkamer')).not.toBeInTheDocument();

    fireEvent.change(filter, { target: { value: 'unassigned' } });
    await waitFor(() => expect(screen.queryByText('Stofzuigen')).not.toBeInTheDocument());
    expect(screen.getAllByText('Niets gepland.').length).toBeGreaterThan(0);
  });

  it('can complete a task and undo it from the overview', async () => {
    const fetchMock = setup();
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: /Afgelopen 3 dagen/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Badkamer' }));
    const undo = await screen.findByRole('button', { name: 'Badkamer ongedaan maken' });
    fireEvent.click(undo);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Afvinken: Badkamer' })).toBeInTheDocument());
    expect(patchBodies(fetchMock, 'o1')).toEqual([{ action: 'complete' }, { action: 'uncomplete' }]);
  });

  it('uses the configured completion control and always shows a green check when done', async () => {
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings({ completionControl: 'thumb' }),
      '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
      '/api/tasks': [makeTask({ _id: 't1', name: 'Huishoudtaak', roomId: 'r1' })],
      '/api/occurrences': () => db,
    });
    storeProfile(ANNA._id);
    db = [makeOccurrence({ _id: 'o3', taskNameSnapshot: 'Afwas', date: '2026-09-15', status: 'done', assigneeId: ANNA._id })];
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: /Afgelopen 3 dagen/ }));
    const done = await screen.findByRole('button', { name: 'Afwas ongedaan maken' });
    expect(done).toHaveClass('bg-success');
  });

  it('moves an item when it is dropped on another day', async () => {
    const fetchMock = setup();
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: 'all' } });
    await screen.findByRole('heading', { name: /^woensdag 16 sep/ });
    act(() => {
      dnd.onDragEnd!({ active: { id: 'occ:o2' }, over: { id: 'day:2026-09-16' } });
    });
    const targetDay = screen.getByTestId('day:2026-09-16');
    await waitFor(() => expect(within(targetDay).getByText('Stofzuigen')).toBeInTheDocument());
    expect(within(targetDay).queryByText(/verplaatst van/)).not.toBeInTheDocument();
    expect(patchBodies(fetchMock, 'o2')).toEqual([{ action: 'reschedule', date: '2026-09-16' }]);
  });

  it('does not offer moving finished items', async () => {
    setup();
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: /Afgelopen 3 dagen/ }));
    await screen.findByRole('heading', { name: /^dinsdag 15 sep/ });
    expect(screen.queryByRole('button', { name: 'Verplaats Afwas' })).not.toBeInTheDocument();
    expect(within(screen.getByTestId('day:2026-09-15')).getByRole('button', { name: 'Afwas ongedaan maken' })).toBeInTheDocument();
  });
});
