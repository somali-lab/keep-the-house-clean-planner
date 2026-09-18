import type { CyclePlan, Slot } from '@huishoudplanner/shared';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { createElement, type ComponentProps } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { makeUser, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeRoom, makeSettings, makeTask, renderWithProviders } from '../../test/render.tsx';
import { PlannerPage } from './PlannerPage.tsx';

// Capture DndContext's onDragEnd so tests can simulate a drop without pointer physics.
const dnd = vi.hoisted(() => ({
  onDragStart: undefined as undefined | ((event: unknown) => void),
  onDragEnd: undefined as undefined | ((event: unknown) => void),
}));
vi.mock('@dnd-kit/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@dnd-kit/core')>();
  return {
    ...actual,
    DndContext: (props: ComponentProps<typeof actual.DndContext>) => {
      dnd.onDragStart = props.onDragStart as (event: unknown) => void;
      dnd.onDragEnd = props.onDragEnd as (event: unknown) => void;
      return createElement(actual.DndContext, props);
    },
  };
});

const ANNA = makeUser({ _id: 'a00000000000000000000001', name: 'Anna', unavailableWeekdays: [2] }); // not on Tuesday
const BRAM = makeUser({
  _id: 'b00000000000000000000002',
  name: 'Bram',
  maxDailyMinutes: { weekday: 60, weekend: 120 },
});
const BADKAMER = makeTask({
  _id: 't1',
  name: 'Badkamer',
  roomId: 'r1',
  intervalKey: '1w',
  durationMinutes: 40,
});
const STOFZUIGEN = makeTask({
  _id: 't2',
  name: 'Stofzuigen',
  roomId: 'r1',
  intervalKey: '1w',
  durationMinutes: 30,
});
const RAMEN = makeTask({
  _id: 't3',
  name: 'Ramen',
  roomId: 'r1',
  intervalKey: 'quarter',
  durationMinutes: 90,
});

const STAMP = '2026-09-14T08:00:00.000Z';
function makePlan(overrides: Partial<CyclePlan> & Pick<CyclePlan, '_id' | 'name'>): CyclePlan {
  return {
    active: false,
    slots: [],
    weekThemes: ['', '', '', ''],
    draft: false,
    source: 'manual',
    proposalId: null,
    rationale: null,
    discarded: false,
    createdAt: STAMP,
    updatedAt: STAMP,
    ...overrides,
  };
}

const slot = (
  taskId: string,
  weekIndex: number,
  weekday: number,
  assigneeId: string | null,
): Slot => ({
  taskId,
  weekIndex,
  weekday,
  assigneeId,
  sortOrder: 0,
});

function setup(plans: CyclePlan[], extra: Record<string, unknown> = {}) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
    '/api/tasks': [BADKAMER, STOFZUIGEN, RAMEN],
    '/api/settings': makeSettings(),
    '/api/cycle-plans': plans,
    ...extra,
  });
}

function drop(activeId: string, overId: string) {
  act(() => {
    dnd.onDragEnd!({ active: { id: activeId }, over: { id: overId } });
  });
}

const putCalls = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'PUT');

async function openPlanManagement() {
  fireEvent.click(await screen.findByRole('button', { name: 'Plannen beheren' }));
}

describe('PlannerPage — drops', () => {
  beforeEach(() => {
    dnd.onDragEnd = undefined;
    dnd.onDragStart = undefined;
  });

  it('refuses a drop on a day the assignee is unavailable and explains why', async () => {
    const fetchMock = setup([makePlan({ _id: 'p1', name: 'Standaard', active: true })]);
    renderWithProviders(<PlannerPage />);
    expect(await screen.findByRole('group', { name: 'Kies een week' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'AI-assistent' })).not.toBeInTheDocument();
    await openPlanManagement();
    expect(screen.getByRole('region', { name: 'AI-assistent' })).toBeInTheDocument();
    expect(
      within(screen.getByTestId(`cell:0:2:${ANNA._id}`)).getByText('Anna niet beschikbaar'),
    ).toBeInTheDocument();
    expect(
      within(screen.getByTestId(`cell:0:2:${ANNA._id}`)).queryByText('0 min'),
    ).not.toBeInTheDocument();

    drop('task:t1', `cell:0:2:${ANNA._id}`);

    expect(screen.getByRole('alert')).toHaveTextContent(
      'Anna kan niet op dinsdag. "Badkamer" is niet geplaatst.',
    );
    expect(
      within(screen.getByTestId(`cell:0:2:${ANNA._id}`)).queryByText('Badkamer'),
    ).not.toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 900));
    expect(putCalls(fetchMock)).toHaveLength(0);
  });

  it('places an accepted drop and saves it after the debounce', async () => {
    const fetchMock = setup([makePlan({ _id: 'p1', name: 'Standaard', active: true })], {
      'PUT /api/cycle-plans/p1/slots': (init: RequestInit) => ({
        plan: makePlan({
          _id: 'p1',
          name: 'Standaard',
          active: true,
          slots: JSON.parse(String(init.body)).slots,
        }),
        warnings: [],
        summary: { tasks: [], days: [], weeks: [] },
      }),
    });
    renderWithProviders(<PlannerPage />);
    await screen.findByRole('group', { name: 'Kies een week' });

    drop('task:t1', `cell:0:2:${BRAM._id}`);

    expect(
      within(screen.getByTestId(`cell:0:2:${BRAM._id}`)).getByText('Badkamer'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(putCalls(fetchMock)).toHaveLength(0);
    await waitFor(() => expect(putCalls(fetchMock)).toHaveLength(1), { timeout: 2000 });
    expect(JSON.parse(String((putCalls(fetchMock)[0]![1] as RequestInit).body))).toEqual({
      slots: [slot('t1', 0, 2, BRAM._id)],
    });
    expect(String(putCalls(fetchMock)[0]![0])).toBe('/api/cycle-plans/p1/slots?sync=true');
    expect(await screen.findByText('Opgeslagen')).toBeInTheDocument();
  });

  it('removes a slot via its button as an alternative to dragging', async () => {
    setup(
      [makePlan({ _id: 'p1', name: 'Standaard', active: true, slots: [slot('t2', 1, 3, null)] })],
      {
        'PUT /api/cycle-plans/p1/slots': {
          plan: makePlan({ _id: 'p1', name: 'Standaard' }),
          warnings: [],
          summary: {},
        },
      },
    );
    renderWithProviders(<PlannerPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Week 2' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Stofzuigen uit plan halen' }));
    expect(
      within(screen.getByTestId('cell:1:3:any')).queryByText('Stofzuigen'),
    ).not.toBeInTheDocument();
  });
});

describe('PlannerPage — budgets and pool', () => {
  it('filters the planning lanes by person without changing the plan', async () => {
    setup([
      makePlan({
        _id: 'p1',
        name: 'Standaard',
        active: true,
        slots: [slot('t1', 0, 1, ANNA._id), slot('t2', 0, 1, BRAM._id), slot('t3', 0, 1, null)],
      }),
    ]);
    renderWithProviders(<PlannerPage />);
    await screen.findByRole('group', { name: 'Kies een week' });

    fireEvent.change(screen.getByLabelText('Filter planner op persoon'), {
      target: { value: BRAM._id },
    });

    expect(screen.queryByTestId(`cell:0:1:${ANNA._id}`)).not.toBeInTheDocument();
    expect(within(screen.getByTestId(`cell:0:1:${BRAM._id}`)).getByText('Stofzuigen')).toBeInTheDocument();
    expect(screen.queryByTestId('cell:0:1:any')).not.toBeInTheDocument();
  });

  it('uses the weekend budget on Saturday and the weekday budget on Monday', async () => {
    setup([
      makePlan({
        _id: 'p1',
        name: 'Standaard',
        active: true,
        slots: [
          slot('t1', 0, 6, BRAM._id),
          slot('t2', 0, 6, BRAM._id),
          slot('t1', 0, 1, BRAM._id),
          slot('t2', 0, 1, BRAM._id),
        ],
      }),
    ]);
    renderWithProviders(<PlannerPage />);

    const saturday = await screen.findByTestId(`cell:0:6:${BRAM._id}`);
    expect(saturday).toHaveTextContent('70 min');
    expect(saturday).not.toHaveTextContent('Boven budget');
    expect(saturday).not.toHaveClass('is-over-budget');

    const monday = screen.getByTestId(`cell:0:1:${BRAM._id}`);
    expect(monday).toHaveTextContent('70 min');
    expect(monday).toHaveTextContent('Boven budget');
    expect(monday).toHaveClass('is-over-budget');

    expect(screen.getByRole('region', { name: 'Week 1' })).toHaveTextContent('Bram: 140 min');
    expect(screen.getByRole('region', { name: 'Week 1' })).toHaveTextContent(
      'Doordeweeks 70/60 min',
    );
    expect(screen.getByRole('region', { name: 'Week 1' })).toHaveTextContent('Weekend 70/120 min');
    expect(within(monday).getAllByText('BR')).toHaveLength(2);
  });

  it('shows placed/required in the pool, n.v.t. for quarter tasks, and hides fully placed tasks', async () => {
    setup([
      makePlan({
        _id: 'p1',
        name: 'Standaard',
        active: true,
        slots: [0, 1, 2, 3].map((w) => slot('t1', w, 4, null)).concat([slot('t2', 0, 4, null)]),
      }),
    ]);
    renderWithProviders(<PlannerPage />);
    const pool = await screen.findByRole('complementary', { name: 'Nog in te plannen' });
    expect(within(pool).queryByText('Badkamer')).not.toBeInTheDocument();
    const stofzuigen = within(pool).getByTestId('pool-t2');
    expect(stofzuigen).toHaveTextContent('Woonkamer');
    expect(within(stofzuigen).getByLabelText('1 van 4 gepland')).toHaveTextContent('1/4');
    expect(stofzuigen).toHaveTextContent('Aantal keer gepland wijkt af van het interval');
    expect(within(pool).getByTestId('pool-t3')).toHaveTextContent('n.v.t.');
  });

  it('filters the task pool by cycle', async () => {
    setup([makePlan({ _id: 'p1', name: 'Standaard', active: true })]);
    renderWithProviders(<PlannerPage />);
    const pool = await screen.findByRole('complementary', { name: 'Nog in te plannen' });

    fireEvent.change(within(pool).getByLabelText('Filter taken op cyclus'), {
      target: { value: 'quarter' },
    });

    expect(within(pool).getByText('Ramen')).toBeInTheDocument();
    expect(within(pool).queryByText('Badkamer')).not.toBeInTheDocument();
    expect(within(pool).queryByText('Stofzuigen')).not.toBeInTheDocument();
  });

  it('collapses the task pool to a compact counter and expands it again', async () => {
    setup([makePlan({ _id: 'p1', name: 'Standaard', active: true })]);
    renderWithProviders(<PlannerPage />);
    const pool = await screen.findByRole('complementary', { name: 'Nog in te plannen' });

    fireEvent.click(within(pool).getByRole('button', { name: 'Nog in te plannen inklappen' }));
    expect(within(pool).queryByLabelText('Filter taken op ruimte')).not.toBeInTheDocument();
    expect(within(pool).getByText('3')).toBeInTheDocument();

    fireEvent.click(within(pool).getByRole('button', { name: 'Nog in te plannen uitklappen' }));
    expect(within(pool).getByLabelText('Filter taken op ruimte')).toBeInTheDocument();
  });

  it('automatically collapses the task pool when every distributable task is planned', async () => {
    setup([
      makePlan({
        _id: 'p1',
        name: 'Standaard',
        active: true,
        slots: [
          ...[0, 1, 2, 3].map((weekIndex) => slot('t1', weekIndex, 1, BRAM._id)),
          ...[0, 1, 2, 3].map((weekIndex) => slot('t2', weekIndex, 4, BRAM._id)),
        ],
      }),
    ]);
    renderWithProviders(<PlannerPage />);

    const pool = await screen.findByRole('complementary', { name: 'Nog in te plannen' });
    await waitFor(() =>
      expect(
        within(pool).getByRole('button', { name: 'Nog in te plannen uitklappen' }),
      ).toBeInTheDocument(),
    );
    expect(within(pool).queryByLabelText('Filter taken op ruimte')).not.toBeInTheDocument();
  });
});

describe('PlannerPage — activation', () => {
  it('moves every planned task back to the pool after confirmation', async () => {
    const original = makePlan({
      _id: 'p1',
      name: 'Standaard',
      active: true,
      slots: [0, 1, 2, 3].map((week) => slot('t1', week, 1, ANNA._id)),
    });
    const fetchMock = setup([original], {
      'PUT /api/cycle-plans/p1/slots': {
        plan: { ...original, slots: [] },
        warnings: [],
        summary: { tasks: [], days: [], weeks: [] },
      },
    });
    renderWithProviders(<PlannerPage />);
    const pool = await screen.findByRole('complementary', { name: 'Nog in te plannen' });
    expect(within(pool).queryByText('Badkamer')).not.toBeInTheDocument();

    await openPlanManagement();
    fireEvent.click(screen.getByRole('button', { name: 'Plan leegmaken' }));
    const dialog = screen.getByRole('dialog', { name: 'Alles terugzetten?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Alles terugzetten' }));

    await waitFor(() =>
      expect(JSON.parse(String((putCalls(fetchMock).at(-1)![1] as RequestInit).body))).toEqual({
        slots: [],
      }),
    );
    expect(await within(pool).findByText('Badkamer')).toBeInTheDocument();
  });

  it('protects the default plan and deletes an inactive copy after confirmation', async () => {
    const defaultPlan = makePlan({ _id: 'p1', name: 'Standaard', active: true });
    const copy = makePlan({ _id: 'p2', name: 'Vakantie' });
    const fetchMock = setup([defaultPlan, copy], {
      'DELETE /api/cycle-plans/p2': { deleted: true },
    });
    renderWithProviders(<PlannerPage />);

    await openPlanManagement();
    expect(await screen.findByRole('button', { name: 'Plan verwijderen' })).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Plan'), { target: { value: 'p2' } });
    const remove = screen.getByRole('button', { name: 'Plan verwijderen' });
    expect(remove).toBeEnabled();
    fireEvent.click(remove);
    const dialog = screen.getByRole('dialog', { name: 'Plan verwijderen?' });
    expect(dialog).toHaveTextContent('“Vakantie” wordt definitief verwijderd');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Definitief verwijderen' }));

    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(
          ([url, init]) =>
            url === '/api/cycle-plans/p2' && (init as RequestInit | undefined)?.method === 'DELETE',
        ),
      ).toBe(true),
    );
    await openPlanManagement();
    expect(screen.getByLabelText('Plan')).toHaveValue('p1');
  });

  it('renames an existing or copied plan', async () => {
    const original = makePlan({ _id: 'p1', name: 'Kopie van Standaard', active: true });
    setup([original], {
      'PATCH /api/cycle-plans/p1': (init: RequestInit) => ({
        ...original,
        name: (JSON.parse(String(init.body)) as { name: string }).name,
      }),
    });
    renderWithProviders(<PlannerPage />);

    await openPlanManagement();
    fireEvent.click(await screen.findByRole('button', { name: 'Naam wijzigen' }));
    fireEvent.change(screen.getByLabelText('Plannaam'), { target: { value: 'Zomerplan' } });
    fireEvent.click(screen.getByRole('button', { name: 'Naam opslaan' }));

    await waitFor(() => expect(screen.getByLabelText('Plan')).toHaveValue('p1'));
    expect(await screen.findByRole('option', { name: 'Zomerplan (actief)' })).toBeInTheDocument();
  });

  it('asks for confirmation with the replacement rule before activating', async () => {
    const fetchMock = setup(
      [
        makePlan({ _id: 'p1', name: 'Standaard', active: true }),
        makePlan({ _id: 'p2', name: 'Zomer' }),
      ],
      {
        'POST /api/cycle-plans/p2/activate': {
          plan: makePlan({ _id: 'p2', name: 'Zomer', active: true }),
          removed: 3,
        },
      },
    );
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.change(await screen.findByLabelText('Plan'), { target: { value: 'p2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Dit plan activeren' }));

    const dialog = screen.getByRole('dialog', { name: 'Plan activeren?' });
    expect(dialog).toHaveTextContent(
      'Afgevinkte, overgeslagen, verplaatste en losse taken blijven staan.',
    );
    fireEvent.click(within(dialog).getByRole('button', { name: 'Activeren' }));

    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => url === '/api/cycle-plans/p2/activate')).toBe(
        true,
      ),
    );
    expect(await screen.findByText('Plan geactiveerd.')).toBeInTheDocument();
  });
});
