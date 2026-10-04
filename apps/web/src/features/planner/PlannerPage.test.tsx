import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { createElement, type ComponentProps } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { makeUser, mockApi, page, problem, storeProfile } from '../../test/fixtures.ts';
import { issueOf, makePlanV2, slotOf, STALE_MESSAGE, staleAnswer, standInValidation } from '../../test/plans.ts';
import { makeRoomV2, makeSettings, makeTaskV2, renderWithProviders } from '../../test/render.tsx';
import type { CyclePlan, PlanIssue } from './api.ts';
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

const ANNA = makeUser({ id: 'a00000000000000000000001', name: 'Anna', unavailableWeekdays: [2] }); // not on Tuesday
const BRAM = makeUser({
  id: 'b00000000000000000000002',
  name: 'Bram',
  maxDailyMinutes: { weekday: 60, weekend: 120 },
});
const BADKAMER = makeTaskV2({ id: 't1', name: 'Badkamer', roomId: 'r1', intervalKey: '1w', durationMinutes: 40 });
const STOFZUIGEN = makeTaskV2({ id: 't2', name: 'Stofzuigen', roomId: 'r1', intervalKey: '1w', durationMinutes: 30 });
const RAMEN = makeTaskV2({ id: 't3', name: 'Ramen', roomId: 'r1', intervalKey: 'quarter', durationMinutes: 90 });
const TASKS = [BADKAMER, STOFZUIGEN, RAMEN];
const USERS = [ANNA, BRAM];

type Fetch = ReturnType<typeof mockApi>;
type SlotsBody = { slots: CyclePlan['slots'] };

/** The plans the fake server holds; routes read and write them, so a save is visible to the next read of the list. */
function setup(initial: CyclePlan[], extra: Record<string, unknown> = {}) {
  storeProfile(ANNA.id);
  const server = { plans: initial };
  const validate = (id: string) => () => standInValidation(server.plans.find((plan) => plan.id === id)?.slots ?? [], TASKS, USERS);
  const routes: Record<string, unknown> = {
    '/api/v2/users': page(USERS),
    '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Woonkamer' })]),
    '/api/v2/tasks': page(TASKS),
    '/api/v2/settings': makeSettings(),
    '/api/v2/cycle-plans': () => page(server.plans),
    'POST /api/v2/cycle-plans/validation': (init: RequestInit) => standInValidation((JSON.parse(String(init.body)) as SlotsBody).slots, TASKS, USERS),
  };
  for (const id of ['p1', 'p2', 'p-ai', 'p-ai-active']) {
    routes[`POST /api/v2/cycle-plans/${id}/validation`] = validate(id);
    routes[`PUT /api/v2/cycle-plans/${id}/slots`] = (init: RequestInit) => {
      const slots = (JSON.parse(String(init.body)) as SlotsBody).slots;
      const stored = server.plans.find((plan) => plan.id === id)!;
      const plan = { ...stored, slots, version: stored.version + 1 };
      server.plans = server.plans.map((candidate) => (candidate.id === id ? plan : candidate));
      return { plan, warnings: [], summary: standInValidation(slots, TASKS, USERS).summary, synchronized: null };
    };
  }
  const fetchMock = mockApi({ ...routes, ...extra });
  return Object.assign(fetchMock, { server });
}

function drop(activeId: string, overId: string) {
  act(() => {
    dnd.onDragEnd!({ active: { id: activeId }, over: { id: overId } });
  });
}

const callsOf = (fetchMock: Fetch, method: string, url: string) =>
  fetchMock.mock.calls
    .filter(([u, init]) => u === url && (init as RequestInit | undefined)?.method === method)
    .map(([, init]) => ({
      ifMatch: ((init as RequestInit).headers as Record<string, string>)['if-match'],
      body: (init as RequestInit).body ? (JSON.parse(String((init as RequestInit).body)) as unknown) : undefined,
    }));

const putCalls = (fetchMock: Fetch) => fetchMock.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'PUT');
const planReads = (fetchMock: Fetch) => fetchMock.mock.calls.filter(([url]) => url === '/api/v2/cycle-plans?limit=200').length;

async function openPlanManagement() {
  fireEvent.click(await screen.findByRole('button', { name: 'Plannen beheren' }));
}

describe('PlannerPage — drops', () => {
  beforeEach(() => {
    dnd.onDragEnd = undefined;
    dnd.onDragStart = undefined;
  });

  it('refuses a drop on a day the assignee is unavailable and explains why', async () => {
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true })]);
    renderWithProviders(<PlannerPage />);
    expect(await screen.findByRole('group', { name: 'Kies een week' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'AI-assistent' })).not.toBeInTheDocument();
    await openPlanManagement();
    expect(screen.getByRole('region', { name: 'AI-assistent' })).toBeInTheDocument();
    expect(within(screen.getByTestId(`cell:0:2:${ANNA.id}`)).getByText('Anna niet beschikbaar')).toBeInTheDocument();
    expect(within(screen.getByTestId(`cell:0:2:${ANNA.id}`)).queryByText('0 min')).not.toBeInTheDocument();

    drop('task:t1', `cell:0:2:${ANNA.id}`);

    expect(screen.getByRole('alert')).toHaveTextContent('Anna kan niet op dinsdag. "Badkamer" is niet geplaatst.');
    expect(within(screen.getByTestId(`cell:0:2:${ANNA.id}`)).queryByText('Badkamer')).not.toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 900));
    expect(putCalls(fetchMock)).toHaveLength(0);
  });

  it('places an accepted drop and saves it after the debounce, with the version of the plan as If-Match', async () => {
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, version: 3 })]);
    renderWithProviders(<PlannerPage />);
    await screen.findByRole('group', { name: 'Kies een week' });

    drop('task:t1', `cell:0:2:${BRAM.id}`);

    expect(within(screen.getByTestId(`cell:0:2:${BRAM.id}`)).getByText('Badkamer')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(putCalls(fetchMock)).toHaveLength(0);
    await waitFor(() => expect(putCalls(fetchMock)).toHaveLength(1), { timeout: 2000 });
    expect(callsOf(fetchMock, 'PUT', '/api/v2/cycle-plans/p1/slots')).toEqual([
      { ifMatch: '"3"', body: { slots: [{ taskId: 't1', weekIndex: 0, weekday: 2, assigneeId: BRAM.id, sortOrder: 0 }] } },
    ]);
    expect(await screen.findByText('Opgeslagen')).toBeInTheDocument();
  });

  it('sends the version of the answer with the next save, so two saves in a row need no re-read', async () => {
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, version: 3 })]);
    renderWithProviders(<PlannerPage />);
    await screen.findByRole('group', { name: 'Kies een week' });

    drop('task:t1', `cell:0:2:${BRAM.id}`);
    await waitFor(() => expect(putCalls(fetchMock)).toHaveLength(1), { timeout: 2000 });
    await screen.findByText('Opgeslagen');
    drop('task:t2', `cell:0:3:${BRAM.id}`);
    await waitFor(() => expect(putCalls(fetchMock)).toHaveLength(2), { timeout: 2000 });

    expect(callsOf(fetchMock, 'PUT', '/api/v2/cycle-plans/p1/slots').map((call) => call.ifMatch)).toEqual(['"3"', '"4"']);
  });

  it('removes a slot via its button as an alternative to dragging', async () => {
    setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, slots: [slotOf('t2', 1, 3, null)] })]);
    renderWithProviders(<PlannerPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Week 2' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Stofzuigen uit plan halen' }));
    expect(within(screen.getByTestId('cell:1:3:any')).queryByText('Stofzuigen')).not.toBeInTheDocument();
  });
});

describe('PlannerPage — edits racing with saves', () => {
  beforeEach(() => {
    dnd.onDragEnd = undefined;
  });

  it('keeps a drop made while the save before it is still in flight', async () => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    const fetchMock: ReturnType<typeof setup> = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, version: 3 })], {
      'PUT /api/v2/cycle-plans/p1/slots': async (init: RequestInit) => {
        // The save after the first one never answers: the test looks at the editor while it is in flight.
        if (putCalls(fetchMock).length > 1) return new Promise<Response>(() => undefined);
        await gate;
        const slots = (JSON.parse(String(init.body)) as SlotsBody).slots;
        const plan = { ...fetchMock.server.plans[0]!, slots, version: 4 };
        fetchMock.server.plans = [plan];
        return { plan, warnings: [], summary: standInValidation(slots, TASKS, USERS).summary, synchronized: null };
      },
    });
    renderWithProviders(<PlannerPage />);
    await screen.findByRole('group', { name: 'Kies een week' });

    drop('task:t1', `cell:0:2:${BRAM.id}`);
    await waitFor(() => expect(putCalls(fetchMock)).toHaveLength(1), { timeout: 2000 });
    drop('task:t2', `cell:0:3:${BRAM.id}`);
    // B is past its debounce and queued behind A when A answers.
    await new Promise((resolve) => setTimeout(resolve, 1000));
    release();

    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(within(screen.getByTestId(`cell:0:2:${BRAM.id}`)).getByText('Badkamer')).toBeInTheDocument();
    expect(within(screen.getByTestId(`cell:0:3:${BRAM.id}`)).getByText('Stofzuigen')).toBeInTheDocument();
    await waitFor(() => expect(putCalls(fetchMock)).toHaveLength(2), { timeout: 2000 });
  });

  it('does not let a drop that is still waiting to be saved overwrite a reset that finished first', async () => {
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, version: 3, slots: [slotOf('t3', 0, 5, null)] })]);
    renderWithProviders(<PlannerPage />);
    await screen.findByRole('group', { name: 'Kies een week' });

    drop('task:t1', `cell:0:2:${BRAM.id}`);
    await openPlanManagement();
    fireEvent.click(screen.getByRole('button', { name: 'Plan leegmaken' }));
    fireEvent.click(within(screen.getByRole('dialog', { name: 'Alles terugzetten?' })).getByRole('button', { name: 'Alles terugzetten' }));
    await screen.findByText('Alle taken staan weer bij “Nog in te plannen”.');
    await new Promise((resolve) => setTimeout(resolve, 1000));

    expect(callsOf(fetchMock, 'PUT', '/api/v2/cycle-plans/p1/slots').map((call) => call.body)).toEqual([{ slots: [] }]);
    expect(fetchMock.server.plans[0]!.slots).toEqual([]);
  });
});

describe('PlannerPage — validation by the server', () => {
  beforeEach(() => {
    dnd.onDragEnd = undefined;
  });

  it('reads the summary of the stored plan from the server while nothing is changed', async () => {
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, slots: [slotOf('t1', 0, 1, ANNA.id)] })]);
    renderWithProviders(<PlannerPage />);
    await screen.findByRole('group', { name: 'Kies een week' });

    expect(callsOf(fetchMock, 'POST', '/api/v2/cycle-plans/p1/validation')).toEqual([{ ifMatch: undefined, body: undefined }]);
    expect(callsOf(fetchMock, 'POST', '/api/v2/cycle-plans/validation')).toEqual([]);
    expect(screen.getByRole('region', { name: 'Totaal voor de hele cyclus' })).toHaveTextContent('Anna: 40 min');
  });

  it('validates the unsaved draft once the person has stopped dropping, and shows the new totals', async () => {
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true })]);
    renderWithProviders(<PlannerPage />);
    await screen.findByRole('group', { name: 'Kies een week' });

    drop('task:t1', `cell:0:1:${BRAM.id}`);
    drop('task:t2', `cell:0:3:${BRAM.id}`);

    await waitFor(() => expect(callsOf(fetchMock, 'POST', '/api/v2/cycle-plans/validation')).toHaveLength(1), { timeout: 2000 });
    expect(callsOf(fetchMock, 'POST', '/api/v2/cycle-plans/validation')[0]!.body).toEqual({
      slots: [slotOf('t1', 0, 1, BRAM.id), slotOf('t2', 0, 3, BRAM.id)],
    });
    expect(await screen.findByText('Totaal 70 min')).toBeInTheDocument();
  });

  it('lists the hard errors the server reports for the plan, named in plain language', async () => {
    const issues: PlanIssue[] = [
      issueOf({ code: 'assignee_unavailable', slotIndex: 0, taskId: 't1', userId: ANNA.id, weekIndex: 0, weekday: 2 }),
      issueOf({ code: 'inactive_task', slotIndex: 1, taskId: 't2' }),
    ];
    setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, slots: [slotOf('t1', 0, 2, ANNA.id), slotOf('t2', 0, 4, null)] })], {
      'POST /api/v2/cycle-plans/p1/validation': standInValidation([], TASKS, USERS, issues),
    });
    renderWithProviders(<PlannerPage />);

    const list = await screen.findByRole('alert', { name: 'Dit plan voldoet nog niet aan de regels' });
    expect(within(list).getByText('Anna kan niet op dinsdag. "Badkamer" staat daar wel.')).toBeInTheDocument();
    expect(within(list).getByText('"Stofzuigen" is niet meer actief.')).toBeInTheDocument();
  });

  it('says so when the plan cannot be checked, and still shows the plan', async () => {
    setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true })], {
      'POST /api/v2/cycle-plans/p1/validation': () => problem(500, 'internal_error'),
    });
    renderWithProviders(<PlannerPage />);

    expect(await screen.findByText('Het plan kon niet worden gecontroleerd. De verdeling hieronder kan afwijken.')).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Kies een week' })).toBeInTheDocument();
  });
});

describe('PlannerPage — plan status', () => {
  it('explains that a draft plan is absent from task overviews until activated', async () => {
    setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true }), makePlanV2({ id: 'p2', name: 'Zomer' })]);
    renderWithProviders(<PlannerPage />);

    await screen.findByRole('group', { name: 'Kies een week' });
    expect(screen.queryByRole('note')).not.toBeInTheDocument();
    await openPlanManagement();
    fireEvent.change(await screen.findByLabelText('Plan'), { target: { value: 'p2' } });

    expect(await screen.findByRole('note')).toHaveTextContent(
      'Dit is een conceptplan. De taken hierin verschijnen niet in Weekoverzicht of Mijn taken totdat je dit plan activeert.',
    );

    fireEvent.change(screen.getByLabelText('Plan'), { target: { value: 'p1' } });
    expect(screen.queryByRole('note')).not.toBeInTheDocument();
  });
});

describe('PlannerPage — budgets and pool', () => {
  it('filters the planning lanes by person without changing the plan', async () => {
    setup([
      makePlanV2({
        id: 'p1',
        name: 'Standaard',
        active: true,
        slots: [slotOf('t1', 0, 1, ANNA.id), slotOf('t2', 0, 1, BRAM.id), slotOf('t3', 0, 1, null)],
      }),
    ]);
    renderWithProviders(<PlannerPage />);
    await screen.findByRole('group', { name: 'Kies een week' });

    fireEvent.change(screen.getByLabelText('Filter planner op persoon'), { target: { value: BRAM.id } });

    expect(screen.queryByTestId(`cell:0:1:${ANNA.id}`)).not.toBeInTheDocument();
    expect(within(screen.getByTestId(`cell:0:1:${BRAM.id}`)).getByText('Stofzuigen')).toBeInTheDocument();
    expect(screen.queryByTestId('cell:0:1:any')).not.toBeInTheDocument();
  });

  it('shows the budgets the server reports: over budget on Monday, not on Saturday', async () => {
    setup([
      makePlanV2({
        id: 'p1',
        name: 'Standaard',
        active: true,
        slots: [slotOf('t1', 0, 6, BRAM.id), slotOf('t2', 0, 6, BRAM.id), slotOf('t1', 0, 1, BRAM.id), slotOf('t2', 0, 1, BRAM.id)],
      }),
    ]);
    renderWithProviders(<PlannerPage />);

    const saturday = await screen.findByTestId(`cell:0:6:${BRAM.id}`);
    expect(saturday).toHaveTextContent('70 min');
    expect(saturday).not.toHaveTextContent('Boven budget');
    expect(saturday).not.toHaveClass('is-over-budget');

    const monday = screen.getByTestId(`cell:0:1:${BRAM.id}`);
    expect(monday).toHaveTextContent('70 min');
    expect(monday).toHaveTextContent('Boven budget');
    expect(monday).toHaveClass('is-over-budget');

    expect(screen.getByRole('region', { name: 'Week 1' })).toHaveTextContent('Bram: 140 min');
    expect(screen.getByRole('region', { name: 'Week 1' })).toHaveTextContent('Doordeweeks 70/60 min');
    expect(screen.getByRole('region', { name: 'Week 1' })).toHaveTextContent('Weekend 70/120 min');
    expect(within(monday).getAllByText('BR')).toHaveLength(2);
  });

  it('shows placed/required in the pool, n.v.t. for quarter tasks, and hides fully placed tasks', async () => {
    setup([
      makePlanV2({
        id: 'p1',
        name: 'Standaard',
        active: true,
        slots: [0, 1, 2, 3].map((w) => slotOf('t1', w, 4, null)).concat([slotOf('t2', 0, 4, null)]),
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
    setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true })]);
    renderWithProviders(<PlannerPage />);
    const pool = await screen.findByRole('complementary', { name: 'Nog in te plannen' });

    fireEvent.change(within(pool).getByLabelText('Filter taken op cyclus'), { target: { value: 'quarter' } });

    expect(within(pool).getByText('Ramen')).toBeInTheDocument();
    expect(within(pool).queryByText('Badkamer')).not.toBeInTheDocument();
    expect(within(pool).queryByText('Stofzuigen')).not.toBeInTheDocument();
  });

  it('searches task names in both pool and scheduled cards without changing totals, and resets visible filters', async () => {
    window.localStorage.clear();
    setup([
      makePlanV2({
        id: 'p1',
        name: 'Standaard',
        active: true,
        slots: [slotOf('t1', 0, 1, ANNA.id), slotOf('t2', 0, 1, BRAM.id)],
      }),
    ]);
    renderWithProviders(<PlannerPage />, { headerReset: true });

    const search = await screen.findByRole('searchbox', { name: 'Zoek taken' });
    expect(screen.getByRole('button', { name: 'Filters van dit scherm resetten' })).toBeDisabled();
    const pool = screen.getByRole('complementary', { name: 'Nog in te plannen' });
    const annaCell = screen.getByTestId(`cell:0:1:${ANNA.id}`);
    const bramCell = screen.getByTestId(`cell:0:1:${BRAM.id}`);
    const totals = () => screen.getByRole('region', { name: 'Totaal voor de hele cyclus' });
    expect(totals()).toHaveTextContent('Anna: 40 min');
    expect(totals()).toHaveTextContent('Bram: 30 min');
    expect(totals()).toHaveTextContent('Totaal 70 min');

    fireEvent.change(search, { target: { value: 'STOF' } });
    expect(within(annaCell).queryByText('Badkamer')).not.toBeInTheDocument();
    expect(within(bramCell).getByText('Stofzuigen')).toBeInTheDocument();
    expect(within(pool).queryByText('Badkamer')).not.toBeInTheDocument();
    expect(window.localStorage.getItem(`huishoudplanner.filters.${ANNA.id}.planner.search`)).toBe('"STOF"');
    expect(totals()).toHaveTextContent('Totaal 70 min');

    fireEvent.click(screen.getByRole('button', { name: 'Filters van dit scherm resetten' }));
    expect(within(annaCell).getByText('Badkamer')).toBeInTheDocument();
    expect(within(pool).getByText('Ramen')).toBeInTheDocument();
  });

  it('collapses the task pool to a compact counter and expands it again', async () => {
    setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true })]);
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
      makePlanV2({
        id: 'p1',
        name: 'Standaard',
        active: true,
        slots: [
          ...[0, 1, 2, 3].map((weekIndex) => slotOf('t1', weekIndex, 1, BRAM.id)),
          ...[0, 1, 2, 3].map((weekIndex) => slotOf('t2', weekIndex, 4, BRAM.id)),
        ],
      }),
    ]);
    renderWithProviders(<PlannerPage />);

    const pool = await screen.findByRole('complementary', { name: 'Nog in te plannen' });
    await waitFor(() => expect(within(pool).getByRole('button', { name: 'Nog in te plannen uitklappen' })).toBeInTheDocument());
    expect(within(pool).queryByLabelText('Filter taken op ruimte')).not.toBeInTheDocument();
  });
});

describe('PlannerPage — plan actions', () => {
  it('moves every planned task back to the pool after confirmation, saving with the version of the plan', async () => {
    const original = makePlanV2({
      id: 'p1',
      name: 'Standaard',
      active: true,
      version: 4,
      slots: [0, 1, 2, 3].map((week) => slotOf('t1', week, 1, ANNA.id)),
    });
    const fetchMock = setup([original]);
    renderWithProviders(<PlannerPage />);
    const pool = await screen.findByRole('complementary', { name: 'Nog in te plannen' });
    expect(within(pool).queryByText('Badkamer')).not.toBeInTheDocument();

    await openPlanManagement();
    fireEvent.click(screen.getByRole('button', { name: 'Plan leegmaken' }));
    const dialog = screen.getByRole('dialog', { name: 'Alles terugzetten?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Alles terugzetten' }));

    await waitFor(() => expect(callsOf(fetchMock, 'PUT', '/api/v2/cycle-plans/p1/slots')).toEqual([{ ifMatch: '"4"', body: { slots: [] } }]));
    expect(await screen.findByText('Alle taken staan weer bij “Nog in te plannen”.')).toBeInTheDocument();
    expect(await within(await screen.findByRole('complementary', { name: 'Nog in te plannen' })).findByText('Badkamer')).toBeInTheDocument();
  });

  it('protects the default plan and deletes an inactive copy after confirmation, with its version', async () => {
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true }), makePlanV2({ id: 'p2', name: 'Vakantie', version: 6 })], {
      'DELETE /api/v2/cycle-plans/p2': { deleted: true },
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

    await waitFor(() => expect(callsOf(fetchMock, 'DELETE', '/api/v2/cycle-plans/p2')).toEqual([{ ifMatch: '"6"', body: undefined }]));
    await openPlanManagement();
    expect(screen.getByLabelText('Plan')).toHaveValue('p1');
  });

  it('renames a plan with the version of the plan and only the name in the body', async () => {
    const original = makePlanV2({ id: 'p1', name: 'Kopie van Standaard', active: true, version: 2 });
    const fetchMock = setup([original], {
      'PATCH /api/v2/cycle-plans/p1': (init: RequestInit) => ({
        ...original,
        version: 3,
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
    expect(callsOf(fetchMock, 'PATCH', '/api/v2/cycle-plans/p1')).toEqual([{ ifMatch: '"2"', body: { name: 'Zomerplan' } }]);
  });

  it('copies the selected plan and opens the copy', async () => {
    const copy = makePlanV2({ id: 'p2', name: 'Kopie van Standaard' });
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true })], { 'POST /api/v2/cycle-plans': copy });
    renderWithProviders(<PlannerPage />);

    await openPlanManagement();
    fireEvent.click(await screen.findByRole('button', { name: 'Kopie maken' }));

    await waitFor(() =>
      expect(callsOf(fetchMock, 'POST', '/api/v2/cycle-plans')).toEqual([{ ifMatch: undefined, body: { name: 'Kopie van Standaard', copyFromId: 'p1' } }]),
    );
    expect(await screen.findByRole('note')).toHaveTextContent('Dit is een conceptplan');
  });

  it('asks for confirmation with the replacement rule before activating', async () => {
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true }), makePlanV2({ id: 'p2', name: 'Zomer' })], {
      'GET /api/v2/cycle-plans/p2/activation-preview': {
        planId: 'p2',
        previewToken: 'a'.repeat(64),
        asOfDate: '2026-09-14',
        removed: [{ occurrenceId: 'o1', cycleIndex: 0, taskId: 't1', taskName: 'Badkamer', date: '2026-09-15', assigneeId: ANNA.id }],
        added: [],
        preserved: {
          done: [],
          skipped: [{ occurrenceId: 'o2', cycleIndex: 0, taskId: 't2', taskName: 'Stofzuigen', date: '2026-09-16', assigneeId: null }],
          moved: [],
          adhoc: [],
        },
      },
      'POST /api/v2/cycle-plans/p2/activation': { plan: makePlanV2({ id: 'p2', name: 'Zomer', active: true }), runId: 'r', removed: 3, generated: [] },
    });
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.change(await screen.findByLabelText('Plan'), { target: { value: 'p2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Dit plan activeren' }));

    const dialog = screen.getByRole('dialog', { name: 'Plan activeren?' });
    expect(await within(dialog).findByText('Badkamer')).toBeInTheDocument();
    expect(dialog).toHaveTextContent('Worden vervangen (1)');
    expect(dialog).toHaveTextContent('Blijven behouden (1)');
    expect(dialog).toHaveTextContent('Anna');
    expect(dialog).toHaveTextContent('Overgeslagen');
    expect(dialog).toHaveTextContent('telt niet mee voor het bepalen van de volgende vervaldatum');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Activeren' }));

    await waitFor(() => expect(callsOf(fetchMock, 'POST', '/api/v2/cycle-plans/p2/activation')).toHaveLength(1));
    // An intent endpoint: the preview token is the precondition, there is no If-Match.
    expect(callsOf(fetchMock, 'POST', '/api/v2/cycle-plans/p2/activation')).toEqual([{ ifMatch: undefined, body: { previewToken: 'a'.repeat(64) } }]);
    expect(await screen.findByText('Plan geactiveerd.')).toBeInTheDocument();
  });

  it('keeps activation disabled when a stale preview cannot be refreshed', async () => {
    let previews = 0;
    const preview = {
      planId: 'p2',
      previewToken: 'a'.repeat(64),
      asOfDate: '2026-09-14',
      removed: [],
      added: [],
      preserved: { done: [], skipped: [], moved: [], adhoc: [] },
    };
    setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true }), makePlanV2({ id: 'p2', name: 'Zomer' })], {
      'GET /api/v2/cycle-plans/p2/activation-preview': () => (++previews > 1 ? problem(503, 'unavailable') : preview),
      'POST /api/v2/cycle-plans/p2/activation': () => problem(409, 'stale_activation_preview'),
    });
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.change(await screen.findByLabelText('Plan'), { target: { value: 'p2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Dit plan activeren' }));
    const dialog = screen.getByRole('dialog', { name: 'Plan activeren?' });
    const activate = within(dialog).getByRole('button', { name: 'Activeren' });
    await waitFor(() => expect(activate).toBeEnabled());
    fireEvent.click(activate);
    await waitFor(() => expect(dialog).toHaveTextContent('Het activatieoverzicht kon niet worden geladen'));
    expect(activate).toBeDisabled();
  });
});

describe('PlannerPage — If-Match (ADR-0022)', () => {
  beforeEach(() => {
    dnd.onDragEnd = undefined;
  });

  it('keeps the unsaved edit in the editor on a 412, says so there, and saves it again with the version read since', async () => {
    let puts = 0;
    // The first save finds the plan changed by someone else (version 4 now); the second one is accepted.
    const fetchMock: ReturnType<typeof setup> = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, version: 3 })], {
      'PUT /api/v2/cycle-plans/p1/slots': (init: RequestInit) => {
        puts += 1;
        const stored = fetchMock.server.plans[0]!;
        if (puts === 1) {
          fetchMock.server.plans = [{ ...stored, version: 4 }];
          return staleAnswer(4)();
        }
        const slots = (JSON.parse(String(init.body)) as SlotsBody).slots;
        const plan = { ...stored, slots, version: 5 };
        fetchMock.server.plans = [plan];
        return { plan, warnings: [], summary: standInValidation(slots, TASKS, USERS).summary, synchronized: null };
      },
    });
    renderWithProviders(<PlannerPage />);
    await screen.findByRole('group', { name: 'Kies een week' });
    const readsBefore = planReads(fetchMock);

    drop('task:t1', `cell:0:2:${BRAM.id}`);
    await waitFor(() => expect(puts).toBe(1), { timeout: 2000 });

    expect(await screen.findByText(STALE_MESSAGE)).toBeInTheDocument();
    await waitFor(() => expect(planReads(fetchMock)).toBeGreaterThan(readsBefore));
    // The edit is still there, and nothing was saved over it.
    expect(within(screen.getByTestId(`cell:0:2:${BRAM.id}`)).getByText('Badkamer')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Opnieuw opslaan' }));
    await waitFor(() => expect(puts).toBe(2));
    const sent = callsOf(fetchMock, 'PUT', '/api/v2/cycle-plans/p1/slots');
    expect(sent.map((call) => call.ifMatch)).toEqual(['"3"', '"4"']);
    expect(sent[1]!.body).toEqual({ slots: [{ taskId: 't1', weekIndex: 0, weekday: 2, assigneeId: BRAM.id, sortOrder: 0 }] });
    await waitFor(() => expect(screen.queryByText(STALE_MESSAGE)).not.toBeInTheDocument());
    expect(await screen.findByText('Opgeslagen')).toBeInTheDocument();
  });

  it('saves a week theme on blur with the version of the plan and only the four themes in the body', async () => {
    const original = makePlanV2({ id: 'p1', name: 'Standaard', active: true, version: 5 });
    const fetchMock = setup([original], {
      'PATCH /api/v2/cycle-plans/p1': (init: RequestInit) => ({ ...original, version: 6, weekThemes: (JSON.parse(String(init.body)) as { weekThemes: string[] }).weekThemes }),
    });
    renderWithProviders(<PlannerPage />);
    const theme = await screen.findByLabelText('Thema week 1');
    fireEvent.change(theme, { target: { value: 'Grote schoonmaak' } });
    fireEvent.blur(theme);

    await waitFor(() => expect(callsOf(fetchMock, 'PATCH', '/api/v2/cycle-plans/p1')).toHaveLength(1));
    expect(callsOf(fetchMock, 'PATCH', '/api/v2/cycle-plans/p1')).toEqual([
      { ifMatch: '"5"', body: { weekThemes: ['Grote schoonmaak', '', '', ''] } },
    ]);
    expect(await screen.findByText('Opgeslagen')).toBeInTheDocument();
  });

  it('says so inside the editor when saving a week theme is stale, and keeps the typed theme', async () => {
    setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, version: 5 })], { 'PATCH /api/v2/cycle-plans/p1': staleAnswer(6) });
    renderWithProviders(<PlannerPage />);
    const theme = await screen.findByLabelText('Thema week 1');
    fireEvent.change(theme, { target: { value: 'Grote schoonmaak' } });
    fireEvent.blur(theme);

    expect(await screen.findByText(STALE_MESSAGE)).toBeInTheDocument();
    expect(screen.getByLabelText('Thema week 1')).toHaveValue('Grote schoonmaak');
    expect(screen.getByRole('button', { name: 'Opnieuw opslaan' })).toBeInTheDocument();
  });

  it('keeps the reset dialog open on a 412, with the message inside it, and clears the message when it is opened again', async () => {
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, version: 3, slots: [slotOf('t1', 0, 1, ANNA.id)] })], {
      'PUT /api/v2/cycle-plans/p1/slots': staleAnswer(4),
    });
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.click(screen.getByRole('button', { name: 'Plan leegmaken' }));
    const dialog = screen.getByRole('dialog', { name: 'Alles terugzetten?' });
    const readsBefore = planReads(fetchMock);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Alles terugzetten' }));

    expect(await within(dialog).findByText(STALE_MESSAGE)).toBeInTheDocument();
    await waitFor(() => expect(planReads(fetchMock)).toBeGreaterThan(readsBefore));

    fireEvent.click(within(dialog).getByRole('button', { name: 'Annuleren' }));
    await openPlanManagement();
    fireEvent.click(screen.getByRole('button', { name: 'Plan leegmaken' }));
    expect(within(screen.getByRole('dialog', { name: 'Alles terugzetten?' })).queryByText(STALE_MESSAGE)).not.toBeInTheDocument();
  });

  it('keeps the delete dialog open on a 412, says so inside it, and deletes with the new version when confirmed again', async () => {
    let deletes = 0;
    const fetchMock: ReturnType<typeof setup> = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true }), makePlanV2({ id: 'p2', name: 'Vakantie', version: 6 })], {
      'DELETE /api/v2/cycle-plans/p2': () => {
        deletes += 1;
        if (deletes > 1) return { deleted: true };
        fetchMock.server.plans = fetchMock.server.plans.map((plan) => (plan.id === 'p2' ? { ...plan, version: 8 } : plan));
        return staleAnswer(8)();
      },
    });
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.change(await screen.findByLabelText('Plan'), { target: { value: 'p2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Plan verwijderen' }));
    const dialog = screen.getByRole('dialog', { name: 'Plan verwijderen?' });
    const readsBefore = planReads(fetchMock);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Definitief verwijderen' }));

    expect(await within(dialog).findByText(STALE_MESSAGE)).toBeInTheDocument();
    await waitFor(() => expect(planReads(fetchMock)).toBeGreaterThan(readsBefore));

    fireEvent.click(within(dialog).getByRole('button', { name: 'Definitief verwijderen' }));
    await waitFor(() => expect(deletes).toBe(2));
    expect(callsOf(fetchMock, 'DELETE', '/api/v2/cycle-plans/p2').map((call) => call.ifMatch)).toEqual(['"6"', '"8"']);
  });

  it('closes the delete dialog when the stale plan turns out to be gone, instead of offering another plan', async () => {
    const fetchMock: ReturnType<typeof setup> = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true }), makePlanV2({ id: 'p2', name: 'Vakantie', version: 6 })], {
      'DELETE /api/v2/cycle-plans/p2': () => {
        fetchMock.server.plans = fetchMock.server.plans.filter((plan) => plan.id !== 'p2');
        return staleAnswer(7)();
      },
    });
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.change(await screen.findByLabelText('Plan'), { target: { value: 'p2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Plan verwijderen' }));
    fireEvent.click(within(screen.getByRole('dialog', { name: 'Plan verwijderen?' })).getByRole('button', { name: 'Definitief verwijderen' }));

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Plan verwijderen?' })).not.toBeInTheDocument());
    expect(callsOf(fetchMock, 'DELETE', '/api/v2/cycle-plans/p1')).toEqual([]);
  });

  it('keeps the new name in the rename form on a 412 and says so next to it', async () => {
    const fetchMock = setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, version: 2 })], {
      'PATCH /api/v2/cycle-plans/p1': staleAnswer(3),
    });
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.click(await screen.findByRole('button', { name: 'Naam wijzigen' }));
    fireEvent.change(screen.getByLabelText('Plannaam'), { target: { value: 'Zomerplan' } });
    const readsBefore = planReads(fetchMock);
    fireEvent.click(screen.getByRole('button', { name: 'Naam opslaan' }));

    const sheet = screen.getByRole('dialog', { name: 'Plannen beheren' });
    expect(await within(sheet).findByText(STALE_MESSAGE)).toBeInTheDocument();
    expect(within(sheet).getByLabelText('Plannaam')).toHaveValue('Zomerplan');
    await waitFor(() => expect(planReads(fetchMock)).toBeGreaterThan(readsBefore));
  });

  it('treats a 428 as a plain error, not as a stale edit', async () => {
    setup([makePlanV2({ id: 'p1', name: 'Standaard', active: true, slots: [slotOf('t1', 0, 1, ANNA.id)] })], {
      'PUT /api/v2/cycle-plans/p1/slots': () => problem(428, 'precondition_required'),
    });
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.click(screen.getByRole('button', { name: 'Plan leegmaken' }));
    const dialog = screen.getByRole('dialog', { name: 'Alles terugzetten?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Alles terugzetten' }));

    expect(await within(dialog).findByText('Het plan kon niet worden leeggemaakt.')).toBeInTheDocument();
    expect(screen.queryByText(STALE_MESSAGE)).not.toBeInTheDocument();
  });
});

describe('PlannerPage — AI drafts', () => {
  const ACTIVE = makePlanV2({ id: 'p1', name: 'Standaard', active: true });
  const DRAFT = makePlanV2({
    id: 'p-ai',
    name: 'AI-voorstel 2026-09-16',
    draft: true,
    source: 'ai',
    proposalId: 'prop-1',
    rationale: ['Week 1 rustig.', 'Week 2 meer badkamer.', 'Week 3 ramen.', 'Week 4 gelijk verdeeld.'],
  });
  const aiSettings = makeSettings({ aiProvider: { type: 'mock', endpoint: null, model: null, timeoutSeconds: null } });
  const proposeButton = () => screen.findByRole('button', { name: 'Voorstel maken' });

  it('selects the new draft automatically after a proposal and announces it', async () => {
    const fetchMock: ReturnType<typeof setup> = setup([ACTIVE], {
      '/api/v2/settings': aiSettings,
      'POST /api/v2/ai/propose-plan': () => {
        fetchMock.server.plans = [ACTIVE, DRAFT];
        return { planId: 'p-ai', proposalId: 'prop-1', warnings: [], rationale: DRAFT.rationale };
      },
    });
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.click(await proposeButton());

    const announcement = await screen.findByText('AI-concept aangemaakt en hieronder geopend. Je actieve plan is niet gewijzigd.');
    expect(announcement).toHaveAttribute('role', 'status');
    expect(await screen.findByRole('region', { name: 'AI-concept' })).toBeInTheDocument();
    // The normal plan view is shown for the draft, where it can be activated or deleted.
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Voorstel maken' })).not.toBeInTheDocument());
    fireEvent.click(within(await screen.findByRole('region', { name: 'AI-concept' })).getByRole('button', { name: 'Plannen beheren' }));
    expect(await screen.findByLabelText('Plan')).toHaveValue('p-ai');
    expect(screen.getByRole('button', { name: 'Dit plan activeren' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Plan verwijderen' })).toBeEnabled();
    // No constraints were typed: the body is empty, not a null.
    expect(callsOf(fetchMock, 'POST', '/api/v2/ai/propose-plan')).toEqual([{ ifMatch: undefined, body: {} }]);
  });

  it('shows the AI draft card with rationale and, right after creation, the warnings', async () => {
    const fetchMock: ReturnType<typeof setup> = setup([ACTIVE], {
      '/api/v2/settings': aiSettings,
      'POST /api/v2/ai/propose-plan': () => {
        fetchMock.server.plans = [ACTIVE, DRAFT];
        return {
          planId: 'p-ai',
          proposalId: 'prop-1',
          warnings: [issueOf({ code: 'interval_mismatch', taskId: 't1', placed: 1, required: 4 })],
          rationale: DRAFT.rationale,
        };
      },
    });
    renderWithProviders(<PlannerPage />);
    expect(screen.queryByRole('region', { name: 'AI-concept' })).not.toBeInTheDocument();
    await openPlanManagement();
    fireEvent.click(await proposeButton());

    const card = await screen.findByRole('region', { name: 'AI-concept' });
    expect(card).toHaveTextContent('Je actieve plan verandert pas als je dit concept activeert');
    await waitFor(() => expect(card).toHaveFocus());
    expect(within(card).getByText('Week 2 meer badkamer.')).toBeInTheDocument();
    expect(within(card).getByText('Aandachtspunten')).toBeInTheDocument();
    expect(within(card).getByText('1 van 4 keer gepland.')).toBeInTheDocument();
    expect(screen.queryByRole('note')).not.toBeInTheDocument();

    // Warnings and the announcement only belong to the moment of creation; the rationale stays with the draft.
    fireEvent.click(within(card).getByRole('button', { name: 'Plannen beheren' }));
    fireEvent.change(await screen.findByLabelText('Plan'), { target: { value: 'p1' } });
    expect(screen.queryByRole('region', { name: 'AI-concept' })).not.toBeInTheDocument();
    expect(screen.queryByText(/AI-concept aangemaakt/)).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Plan'), { target: { value: 'p-ai' } });
    const again = await screen.findByRole('region', { name: 'AI-concept' });
    expect(within(again).getByText('Week 4 gelijk verdeeld.')).toBeInTheDocument();
    expect(within(again).queryByText('Aandachtspunten')).not.toBeInTheDocument();
  });

  it('shows no AI card for a former AI plan that is no longer a draft', async () => {
    setup([ACTIVE, { ...DRAFT, draft: false }, { ...DRAFT, id: 'p-ai-active', active: true, draft: false }]);
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.change(await screen.findByLabelText('Plan'), { target: { value: 'p-ai' } });

    expect(await screen.findByRole('note')).toHaveTextContent('Dit is een conceptplan');
    expect(screen.queryByRole('region', { name: 'AI-concept' })).not.toBeInTheDocument();
  });

  it('keeps the selection and shows the validation error when the proposal is rejected', async () => {
    setup([ACTIVE, DRAFT], {
      '/api/v2/settings': aiSettings,
      'POST /api/v2/ai/propose-plan': () => problem(422, 'ai_invalid_plan', 'invalid', { errors: ['assignee_unavailable (slot 0)'] }),
    });
    renderWithProviders(<PlannerPage />);
    await openPlanManagement();
    fireEvent.change(await screen.findByLabelText('Plan'), { target: { value: 'p1' } });
    fireEvent.click(await proposeButton());

    expect(await screen.findByRole('alert')).toHaveTextContent('Het voorstel voldeed niet aan de regels, ook niet na een tweede poging.');
    expect(screen.getByLabelText('Plan')).toHaveValue('p1');
    expect(screen.queryByRole('region', { name: 'AI-concept' })).not.toBeInTheDocument();
    expect(screen.queryByText(/AI-concept aangemaakt/)).not.toBeInTheDocument();
  });
});
