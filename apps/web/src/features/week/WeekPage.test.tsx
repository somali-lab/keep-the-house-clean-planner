import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { createElement, type ComponentProps } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApiWarning, Occurrence } from '../../api/index.ts';
import { ANNA, BRAM, mockApi, page, problem, storeProfile, v2Basics } from '../../test/fixtures.ts';
import { makeOccurrenceV2, makeRoom, makeSettings, makeTask, renderWithProviders } from '../../test/render.tsx';
import { resetProfileStore } from '../../identity/profileStore.ts';
import { WeekPage } from './WeekPage.tsx';
import { groupByDay, matchesTaskName, movedTo, overviewDays, shortDay, weekDays, weekRangeLabel } from './weekModel.ts';

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

let db: Occurrence[];
let nextWarnings: ApiWarning[] = [];

function setup(settings = makeSettings(), users = [ANNA, BRAM]) {
  storeProfile(ANNA._id);
  db = [
    makeOccurrenceV2({ id: 'o1', taskNameSnapshot: 'Badkamer', date: '2026-09-15', assigneeId: ANNA._id }),
    makeOccurrenceV2({ id: 'o2', taskNameSnapshot: 'Stofzuigen', date: '2026-09-17', plannedDate: '2026-09-16', movedFrom: '2026-09-16', assigneeId: BRAM._id }),
    makeOccurrenceV2({ id: 'o3', taskNameSnapshot: 'Afwas', date: '2026-09-15', status: 'done', assigneeId: ANNA._id }),
  ];
  type Body = { date?: string; completedBy?: string | null; takeOver?: boolean | null };
  const update = (id: string, name: 'reschedule' | 'complete' | 'uncomplete') => (init: RequestInit | undefined) => {
    const body = (init?.body ? JSON.parse(String(init.body)) : {}) as Body;
    const current = db.find((o) => o.id === id)!;
    const updated =
      name === 'reschedule'
        ? movedTo(current, body.date!)
        : name === 'complete'
          ? {
              ...current,
              status: 'done' as const,
              assigneeId: body.takeOver ? ANNA._id : current.assigneeId,
              completedBy: body.takeOver ? ANNA._id : (body.completedBy ?? current.assigneeId ?? ANNA._id),
              completedAt: NOW.toISOString(),
              statusBeforeCompletion: 'open' as const,
            }
          : { ...current, status: 'open' as const, completedBy: null, completedAt: null, statusBeforeCompletion: null };
    db = db.map((o) => (o.id === id ? updated : o));
    return { ...updated, warnings: nextWarnings };
  };
  return mockApi({
    '/api/users': users,
    '/api/settings': settings,
    '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
    '/api/tasks': [makeTask({ _id: 't1', name: 'Huishoudtaak', roomId: 'r1' })],
    ...v2Basics(settings.cycleAnchorDate),
    '/api/v2/occurrences': () => page(db),
    ...Object.fromEntries(
      ['o1', 'o2'].flatMap((id) =>
        (['reschedule', 'complete', 'uncomplete'] as const).map((name) => [
          `POST /api/v2/occurrences/${id}/${name}`,
          update(id, name),
        ]),
      ),
    ),
  });
}

/** The bodies of the requests that sent the intent to one occurrence, in order. */
const intentBodies = (fetchMock: ReturnType<typeof mockApi>, id: string, name: string) =>
  fetchMock.mock.calls
    .filter(([u, init]) => u === `/api/v2/occurrences/${id}/${name}` && init?.method === 'POST')
    .map(([, init]) => (init?.body ? JSON.parse(String(init.body)) : null));

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
    const occ = makeOccurrenceV2({ id: 'x', date: '2026-09-15', plannedDate: '2026-09-15' });
    expect(movedTo(occ, '2026-09-18')).toMatchObject({ date: '2026-09-18', movedFrom: '2026-09-15' });
    expect(movedTo(movedTo(occ, '2026-09-18'), '2026-09-15').movedFrom).toBeNull();
    expect(groupByDay([occ], ['2026-09-15', '2026-09-16']).map((d) => d.items.length)).toEqual([1, 0]);
  });

  it('matches task-name substrings without case or accent differences', () => {
    expect(matchesTaskName('Café schoonmaken', 'cafe')).toBe(true);
    expect(matchesTaskName('Badkamer', 'KAM')).toBe(true);
    expect(matchesTaskName('Badkamer', 'keuken')).toBe(false);
  });
});

describe('WeekPage', () => {
  beforeEach(() => {
    nextWarnings = [];
    dnd.onDragEnd = undefined;
    window.localStorage.clear();
    resetProfileStore();
  });

  it('uses three columns for wider screens', async () => {
    setup();
    renderWithProviders(<WeekPage now={NOW} />);

    expect(await screen.findByTestId('week-day-grid')).toHaveClass('lg:grid-cols-3');
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
    expect(await screen.findByRole('heading', { name: 'Weekoverzicht' })).toHaveClass('sr-only');
    expect(screen.queryByRole('heading', { name: '12-daags overzicht' })).not.toBeInTheDocument();
    await screen.findByRole('heading', { name: 'woensdag 16 sep Vandaag' });

    fireEvent.click(screen.getByRole('button', { name: 'Volgende periode' }));
    expect(await screen.findByRole('heading', { name: 'woensdag 23 sep' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Volgende periode' }));
    expect(await screen.findByRole('heading', { name: 'woensdag 30 sep' })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Rond vandaag' }));
    expect(await screen.findByRole('heading', { name: 'woensdag 16 sep Vandaag' })).toBeInTheDocument();
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

  it('searches task names, shows cycle weeks, and persists week choices per profile', async () => {
    setup();
    db[0] = { ...db[0]!, taskNameSnapshot: 'Café badkamer', date: '2026-09-24' };
    const first = renderWithProviders(<WeekPage now={NOW} />, { headerReset: true });
    fireEvent.click(await screen.findByRole('button', { name: /Afgelopen 3 dagen/ }));
    expect(screen.getByRole('button', { name: 'Filters van dit scherm resetten' })).toBeEnabled();
    const search = screen.getByRole('textbox', { name: 'Zoek taken' });
    fireEvent.change(search, { target: { value: 'CAFE' } });
    expect(await screen.findByText('Café badkamer')).toBeInTheDocument();
    expect(screen.queryByText('Stofzuigen')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Toon cyclusweek' }));
    expect(screen.getByTestId('day:2026-09-16').querySelector('h2')).toHaveTextContent('Cyclusweek 1');
    fireEvent.click(screen.getByRole('button', { name: 'Volgende periode' }));
    expect(await screen.findByRole('heading', { name: /woensdag 23 sep/ })).toBeInTheDocument();
    first.unmount();

    const persistedView = renderWithProviders(<WeekPage now={NOW} />, { headerReset: true });
    expect(await screen.findByRole('heading', { name: /woensdag 23 sep/ })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Zoek taken' })).toHaveValue('CAFE');
    expect(screen.getByRole('button', { name: 'Toon cyclusweek' })).toHaveAttribute('aria-pressed', 'true');
    expect(await screen.findByText('Café badkamer')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Filters van dit scherm resetten' }));
    expect(await screen.findByRole('textbox', { name: 'Zoek taken' })).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Toon cyclusweek' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByRole('button', { name: 'Filters van dit scherm resetten' })).toBeDisabled();

    // A different selected profile gets its own defaults and period state.
    persistedView.unmount();
    storeProfile(BRAM._id);
    resetProfileStore();
    renderWithProviders(<WeekPage now={NOW} />);
    expect(await screen.findByRole('heading', { name: /woensdag 16 sep Vandaag/ })).toBeInTheDocument();
    expect(screen.getByLabelText('Filter op persoon')).toHaveValue(BRAM._id);
  });

  it('can complete a task and undo it from the overview', async () => {
    const fetchMock = setup();
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: /Afgelopen 3 dagen/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Badkamer' }));
    const undo = await screen.findByRole('button', { name: 'Badkamer ongedaan maken' });
    fireEvent.click(undo);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Afvinken: Badkamer' })).toBeInTheDocument());
    expect(intentBodies(fetchMock, 'o1', 'complete')).toEqual([{ completedBy: null, takeOver: null }]);
    expect(intentBodies(fetchMock, 'o1', 'uncomplete')).toEqual([null]);
  });

  it('shows recorded extra work with an Extra label and retracts it on undo', async () => {
    storeProfile(ANNA._id);
    db = [
      makeOccurrenceV2({
        id: 'o-extra',
        taskNameSnapshot: 'Ramen',
        date: '2026-09-16',
        assigneeId: ANNA._id,
        status: 'done',
        completedBy: ANNA._id,
        origin: 'adhoc',
        recordedDone: true,
      }),
    ];
    const fetchMock = mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings(),
      '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
      '/api/tasks': [makeTask({ _id: 't1', name: 'Huishoudtaak', roomId: 'r1' })],
      ...v2Basics(),
      '/api/v2/occurrences': () => page(db),
      'POST /api/v2/occurrences/o-extra/retraction': () => {
        db = [];
        return { retracted: true, id: 'o-extra' };
      },
    });
    renderWithProviders(<WeekPage now={NOW} />);
    const undo = await screen.findByRole('button', { name: 'Ramen ongedaan maken' });
    expect(within(undo.closest('li')!).getByText('Extra')).toBeInTheDocument();
    fireEvent.click(undo);
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Ramen ongedaan maken' })).not.toBeInTheDocument());
    expect(intentBodies(fetchMock, 'o-extra', 'retraction')).toHaveLength(1);
    expect(intentBodies(fetchMock, 'o-extra', 'uncomplete')).toEqual([]);
  });

  it('offers no undo for recorded work of an earlier day, because retracting is only an undo of today', async () => {
    storeProfile(ANNA._id);
    db = [
      makeOccurrenceV2({ id: 'o-old', taskNameSnapshot: 'Ramen', date: '2026-09-15', assigneeId: ANNA._id, status: 'done', completedBy: ANNA._id, origin: 'adhoc', recordedDone: true }),
      makeOccurrenceV2({ id: 'o-planned', taskNameSnapshot: 'Afwas', date: '2026-09-15', assigneeId: ANNA._id, status: 'done', completedBy: ANNA._id }),
    ];
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings(),
      '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
      '/api/tasks': [makeTask({ _id: 't1', name: 'Huishoudtaak', roomId: 'r1' })],
      ...v2Basics(),
      '/api/v2/occurrences': () => page(db),
    });
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: /Afgelopen 3 dagen/ }));
    expect(await screen.findByRole('button', { name: 'Afwas ongedaan maken' })).toBeInTheDocument();
    expect(screen.getByText('Ramen')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ramen ongedaan maken' })).not.toBeInTheDocument();
  });

  it('only offers to take the task over when its assignee is no longer active', async () => {
    const fetchMock = setup(makeSettings(), [ANNA, { ...BRAM, active: false }]);
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: 'all' } });
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Stofzuigen' }));

    expect(screen.getByRole('alertdialog')).toHaveTextContent('Bram de Vries, die niet meer actief is');
    expect(screen.queryByRole('button', { name: 'Namens Bram de Vries afvinken' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Ik heb de taak overgenomen' }));
    await waitFor(() => expect(intentBodies(fetchMock, 'o2', 'complete')).toEqual([{ completedBy: null, takeOver: true }]));
  });

  it("asks how to complete another person's task and can take it over", async () => {
    const fetchMock = setup();
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: BRAM._id } });
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Stofzuigen' }));

    expect(screen.getByRole('alertdialog')).toHaveTextContent('Deze taak staat op naam van Bram de Vries');
    fireEvent.click(screen.getByRole('button', { name: 'Ik heb de taak overgenomen' }));

    await waitFor(() => expect(db.find((occurrence) => occurrence.id === 'o2')).toMatchObject({
      status: 'done',
      assigneeId: ANNA._id,
      completedBy: ANNA._id,
    }));
    expect(intentBodies(fetchMock, 'o2', 'complete')).toEqual([{ completedBy: null, takeOver: true }]);
  });

  it('hides occurrences before the first cycle and explains that the cycle has not started', async () => {
    const fetchMock = setup(makeSettings({ cycleAnchorDate: '2026-09-21' }));
    db = [
      makeOccurrenceV2({ id: 'o1', taskNameSnapshot: 'Te vroeg', date: '2026-09-20', assigneeId: ANNA._id, cycleIndex: -1 }),
      makeOccurrenceV2({ id: 'o2', taskNameSnapshot: 'Vanaf de start', date: '2026-09-21', assigneeId: ANNA._id, cycleIndex: 0 }),
    ];
    renderWithProviders(<WeekPage now={new Date('2026-09-20T08:00:00Z')} />);

    const today = await screen.findByTestId('day:2026-09-20');
    expect(within(today).getByText('De plancyclus is nog niet begonnen.')).toBeInTheDocument();
    expect(screen.queryByText('Te vroeg')).not.toBeInTheDocument();
    expect(await screen.findByText('Vanaf de start')).toBeInTheDocument();
    expect(screen.getByTestId('week-summary')).toHaveTextContent('1 taken in dit overzicht');

    act(() => {
      dnd.onDragEnd!({ active: { id: 'occ:o2' }, over: { id: 'day:2026-09-20' } });
    });
    expect(intentBodies(fetchMock, 'o2', 'reschedule')).toEqual([]);
  });

  it('uses the configured completion control and always shows a green check when done', async () => {
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings({ completionControl: 'thumb' }),
      '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
      '/api/tasks': [makeTask({ _id: 't1', name: 'Huishoudtaak', roomId: 'r1' })],
      ...v2Basics(),
      '/api/v2/occurrences': () => page(db),
    });
    storeProfile(ANNA._id);
    db = [makeOccurrenceV2({ id: 'o3', taskNameSnapshot: 'Afwas', date: '2026-09-15', status: 'done', assigneeId: ANNA._id })];
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
    expect(intentBodies(fetchMock, 'o2', 'reschedule')).toEqual([{ date: '2026-09-16' }]);
  });

  it('shows the warnings the server returns for a move', async () => {
    nextWarnings = [{ code: 'assignee_unavailable', message: 'unavailable', details: { userId: BRAM._id, weekday: 3 } as unknown as ApiWarning['details'] }];
    setup();
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: 'all' } });
    await screen.findByRole('heading', { name: /^woensdag 16 sep/ });
    act(() => {
      dnd.onDragEnd!({ active: { id: 'occ:o2' }, over: { id: 'day:2026-09-16' } });
    });
    expect(await screen.findByText('Bram de Vries is op woensdag niet beschikbaar. De taak is wel verplaatst.')).toBeInTheDocument();
  });

  it('puts an item back when the server refuses the move with a problem', async () => {
    setup();
    const routes = {
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings(),
      '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
      '/api/tasks': [makeTask({ _id: 't1', name: 'Huishoudtaak', roomId: 'r1' })],
      ...v2Basics(),
      '/api/v2/occurrences': () => page(db),
      'POST /api/v2/occurrences/o2/reschedule': () => problem(409, 'cycle_not_generated', 'No cycle.'),
    };
    mockApi(routes);
    renderWithProviders(<WeekPage now={NOW} />);
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: 'all' } });
    await screen.findByRole('heading', { name: /^woensdag 16 sep/ });
    act(() => {
      dnd.onDragEnd!({ active: { id: 'occ:o2' }, over: { id: 'day:2026-09-16' } });
    });
    expect(await screen.findByRole('alert')).toHaveTextContent('Verplaatsen lukte niet');
    await waitFor(() => expect(within(screen.getByTestId('day:2026-09-17')).getByText('Stofzuigen')).toBeInTheDocument());
    expect(within(screen.getByTestId('day:2026-09-16')).queryByText('Stofzuigen')).not.toBeInTheDocument();
  });

  it('reads the cycle week of each day from the server calendar', async () => {
    setup();
    renderWithProviders(<WeekPage now={NOW} />);
    await screen.findByRole('button', { name: 'Toon cyclusweek' });
    fireEvent.click(screen.getByRole('button', { name: 'Toon cyclusweek' }));
    expect(screen.getByTestId('day:2026-09-16').querySelector('h2')).toHaveTextContent('Cyclusweek 1');
    fireEvent.click(screen.getByRole('button', { name: 'Volgende periode' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Volgende periode' }));
    expect((await screen.findByTestId('day:2026-09-30')).querySelector('h2')).toHaveTextContent('Cyclusweek 3');
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
