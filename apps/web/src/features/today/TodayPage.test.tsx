import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import type { Occurrence } from '../../api/index.ts';
import { ANNA, BRAM, mockApi, page, storeProfile, v2Basics } from '../../test/fixtures.ts';
import { makeOccurrenceV2, makeRoom, makeSettings, makeTask, renderWithProviders } from '../../test/render.tsx';
import { applyOptimistic, type OccurrenceAction } from './api.ts';
import { TodayPage } from './TodayPage.tsx';

const NOW = new Date('2026-09-16T08:00:00Z'); // Wednesday 10:00 Amsterdam
const TODAY = '2026-09-16';

let db: Occurrence[];
let failNext = false;

type IntentBody = { completedBy?: string | null; takeOver?: boolean | null; reason?: string | null; assigneeId?: string | null };

/** The v2 endpoint of each intent, and how its request body maps onto the action. */
const INTENTS = {
  complete: (id: string, body: IntentBody): OccurrenceAction => ({
    id,
    kind: 'complete',
    ...(body.completedBy ? { completedBy: body.completedBy } : {}),
    ...(body.takeOver ? { takeOver: true as const } : {}),
  }),
  uncomplete: (id: string): OccurrenceAction => ({ id, kind: 'uncomplete' }),
  skip: (id: string, body: IntentBody): OccurrenceAction => ({ id, kind: 'skip', ...(body.reason ? { reason: body.reason } : {}) }),
  assignment: (id: string, body: IntentBody): OccurrenceAction => ({ id, kind: 'assign', assigneeId: body.assigneeId ?? null }),
  claim: (id: string): OccurrenceAction => ({ id, kind: 'claim' }),
} as const;

/** Tiny in-memory server so refetches after a mutation reflect the change. */
function setup(settings = makeSettings(), users = [ANNA, BRAM]) {
  storeProfile(ANNA._id);
  db = [
    makeOccurrenceV2({ id: 'o-other', taskId: 't1', taskNameSnapshot: 'Stofzuigen', date: TODAY, assigneeId: BRAM._id }),
    makeOccurrenceV2({ id: 'o-mine', taskId: 't2', taskNameSnapshot: 'Badkamer', date: TODAY, assigneeId: ANNA._id, durationMinutesSnapshot: 30 }),
    makeOccurrenceV2({ id: 'o-late', taskId: 't3', taskNameSnapshot: 'Ramen', date: '2026-09-14', plannedDate: '2026-09-14', assigneeId: ANNA._id, isOverdue: true }),
    makeOccurrenceV2({ id: 'o-free', taskId: 't4', taskNameSnapshot: 'Wastafel', date: TODAY, assigneeId: null }),
    makeOccurrenceV2({ id: 'o-tomorrow', taskId: 't5', taskNameSnapshot: 'Keuken morgen', date: '2026-09-17', assigneeId: ANNA._id }),
    makeOccurrenceV2({ id: 'o-after-tomorrow', taskId: 't6', taskNameSnapshot: 'Was overmorgen', date: '2026-09-18', assigneeId: ANNA._id }),
  ];
  const intent = (id: string, name: keyof typeof INTENTS) => (init: RequestInit | undefined) => {
    if (failNext) {
      failNext = false;
      throw new Error('boom');
    }
    const body = (init?.body ? JSON.parse(String(init.body)) : {}) as IntentBody;
    const current = db.find((o) => o.id === id)!;
    const next = applyOptimistic(current, INTENTS[name](id, body), { profileId: ANNA._id, todayKey: TODAY, now: NOW });
    db = db.map((o) => (o.id === id ? next : o));
    return next;
  };
  return mockApi({
    '/api/users': users,
    '/api/settings': settings,
    '/api/rooms': [makeRoom({ _id: 'r1', name: 'Badkamer-ruimte' })],
    '/api/tasks': [makeTask({ _id: 't2', name: 'Badkamer', roomId: 'r1' })],
    ...v2Basics(settings.cycleAnchorDate),
    '/api/v2/occurrences': (_init: RequestInit | undefined, url: string) => {
      const query = new URL(url, 'http://localhost').searchParams;
      const from = query.get('from') ?? '';
      const to = query.get('to') ?? '';
      return page(db.filter((occurrence) => occurrence.date >= from && occurrence.date <= to));
    },
    ...Object.fromEntries(
      db.flatMap((o) =>
        (Object.keys(INTENTS) as (keyof typeof INTENTS)[]).map((name) => [
          `POST /api/v2/occurrences/${o.id}/${name}`,
          intent(o.id, name),
        ]),
      ),
    ),
    'POST /api/v2/occurrences': (init: RequestInit | undefined) => {
      const body = JSON.parse(String(init?.body)) as { taskId: string; date: string; assigneeId: string };
      const created = makeOccurrenceV2({
        id: 'o-recorded',
        taskId: body.taskId,
        taskNameSnapshot: 'Badkamer',
        date: body.date,
        assigneeId: body.assigneeId,
        status: 'done',
        completedAt: NOW.toISOString(),
        completedBy: body.assigneeId,
        origin: 'adhoc',
        recordedDone: true,
      });
      db = [...db, created];
      return created;
    },
    'POST /api/v2/occurrences/o-recorded/retraction': () => {
      db = db.filter((o) => o.id !== 'o-recorded');
      return { retracted: true, id: 'o-recorded' };
    },
  });
}

/** The bodies of the requests that sent the intent to one occurrence, in order. */
const intentBodies = (fetchMock: ReturnType<typeof mockApi>, id: string, name: string) =>
  fetchMock.mock.calls
    .filter(([url, init]) => url === `/api/v2/occurrences/${id}/${name}` && init?.method === 'POST')
    .map(([, init]) => (init?.body ? JSON.parse(String(init.body)) : null));

const sectionTitles = () => screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent);
const section = (name: string) => screen.getByRole('region', { name });
/** Optimistic updates land one tick after the click (onMutate awaits cancelQueries). */
const inSection = async (name: string, text: string) =>
  waitFor(() => within(section(name)).getByText(text));

describe('TodayPage', () => {
  beforeEach(() => {
    failNext = false;
    for (const key of Object.keys(localStorage)) if (key.startsWith('huishoudplanner.filters.')) localStorage.removeItem(key);
  });

  it('shows the active profile by default and can switch to another person or everyone', async () => {
    const fetchMock = setup();
    renderWithProviders(<TodayPage now={NOW} />);
    await screen.findByRole('heading', { name: 'Mijn taken' });

    expect(screen.getByLabelText('Filter op persoon')).toHaveValue(ANNA._id);
    expect(sectionTitles()).toEqual(['Mijn taken', 'Achterstallig']);
    expect(within(section('Mijn taken')).getByText('Badkamer')).toBeInTheDocument();
    expect(within(section('Mijn taken')).getByText(/Badkamer-ruimte · 30 min · Anna/)).toBeInTheDocument();
    expect(screen.queryByText('Wastafel')).not.toBeInTheDocument();
    expect(screen.queryByText('Stofzuigen')).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Filter op persoon'), { target: { value: BRAM._id } });
    expect(await screen.findByRole('heading', { name: 'Taken van Bram de Vries' })).toBeInTheDocument();
    expect(within(section('Taken van Bram de Vries')).getByText('Stofzuigen')).toBeInTheDocument();
    expect(screen.queryByText('Badkamer')).not.toBeInTheDocument();

    // Only the everyone view switches to two columns on wide screens.
    expect(screen.getByTestId('today-sections')).not.toHaveClass('lg:columns-2');
    fireEvent.change(screen.getByLabelText('Filter op persoon'), { target: { value: 'unassigned' } });
    expect(screen.getByTestId('today-sections')).not.toHaveClass('lg:columns-2');
    fireEvent.change(screen.getByLabelText('Filter op persoon'), { target: { value: 'all' } });
    expect(screen.getByTestId('today-sections')).toHaveClass('lg:columns-2');
    expect(sectionTitles()).toEqual(['Mijn taken', 'Nog niet opgepakt', 'Van anderen', 'Achterstallig']);
    expect(within(section('Nog niet opgepakt')).getByText('Wastafel')).toBeInTheDocument();
    expect(within(section('Van anderen')).getByText('Stofzuigen')).toBeInTheDocument();
    const late = within(section('Achterstallig')).getByText('Ramen').closest('li')!;
    expect(late).toHaveTextContent('Achterstallig — gepland op ma 14-09');

    const url = String(fetchMock.mock.calls.find(([u]) => String(u).startsWith('/api/v2/occurrences'))![0]);
    expect(url).toBe('/api/v2/occurrences?from=2026-07-22&to=2026-09-16&limit=500');
  });

  it('persists the selected person and can reset it to the active profile', async () => {
    setup();
    const firstRender = renderWithProviders(<TodayPage now={NOW} />, { headerReset: true });
    await screen.findByRole('heading', { name: 'Mijn taken' });
    // The person filter starts at its default, so the header reset has nothing to do.
    expect(screen.getByRole('button', { name: 'Filters van dit scherm resetten' })).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Filter op persoon'), { target: { value: BRAM._id } });
    firstRender.unmount();
    renderWithProviders(<TodayPage now={NOW} />, { headerReset: true });
    expect(await screen.findByLabelText('Filter op persoon')).toHaveValue(BRAM._id);
    fireEvent.click(screen.getByRole('button', { name: 'Filters van dit scherm resetten' }));
    expect(screen.getByLabelText('Filter op persoon')).toHaveValue(ANNA._id);
    expect(screen.getByText('Filters gereset')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Filters van dit scherm resetten' })).toBeDisabled();
  });

  it('shows the cycle week and browses to tomorrow and the day after tomorrow', async () => {
    setup();
    renderWithProviders(<TodayPage now={NOW} />);

    expect(await screen.findByText('woensdag 16 sep · Cyclusweek 1')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Vandaag' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Vorige dag' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Morgen' }));
    expect(await screen.findByText('donderdag 17 sep · Cyclusweek 1')).toBeInTheDocument();
    expect(await screen.findByText('Keuken morgen')).toBeInTheDocument();
    expect(screen.queryByText('Badkamer')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Overmorgen' }));
    expect(await screen.findByText('vrijdag 18 sep · Cyclusweek 1')).toBeInTheDocument();
    expect(await screen.findByText('Was overmorgen')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Volgende dag' }));
    expect(await screen.findByText('zaterdag 19 sep · Cyclusweek 1')).toBeInTheDocument();
    expect(screen.getByText('Geen open taken voor deze dag.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Vorige dag' }));
    expect(await screen.findByText('Was overmorgen')).toBeInTheDocument();
  });

  it('does not show pre-cycle tasks as overdue before the cycle starts', async () => {
    setup(makeSettings({ cycleAnchorDate: '2026-09-21' }));
    renderWithProviders(<TodayPage now={NOW} />);

    expect(await screen.findByText('woensdag 16 sep · Cyclus start op ma 21-09')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Achterstallig' })).not.toBeInTheDocument();
    expect(within(section('Mijn taken')).getByText('Badkamer')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Morgen' }));
    expect(await screen.findByText('donderdag 17 sep · Cyclus start op ma 21-09')).toBeInTheDocument();
  });

  it('checks off with one tap, and undo restores the previous status', async () => {
    const fetchMock = setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Badkamer' }));

    // moved to finished, snackbar offers undo
    expect(await inSection('Afgerond', 'Badkamer')).toBeInTheDocument();
    const snackbar = screen.getByRole('status');
    expect(snackbar).toHaveTextContent('"Badkamer" afgevinkt.');
    await waitFor(() => expect(db.find((o) => o.id === 'o-mine')?.status).toBe('done'));

    fireEvent.click(within(snackbar).getByRole('button', { name: 'Ongedaan maken' }));
    expect(await inSection('Mijn taken', 'Badkamer')).toBeInTheDocument();
    await waitFor(() => expect(db.find((o) => o.id === 'o-mine')?.status).toBe('open'));
    expect(screen.queryByRole('region', { name: 'Afgerond' })).not.toBeInTheDocument();
    expect(intentBodies(fetchMock, 'o-mine', 'complete')).toEqual([{ completedBy: null, takeOver: null }]);
    expect(intentBodies(fetchMock, 'o-mine', 'uncomplete')).toEqual([null]);
  });

  it("asks before checking off another person's task and can do so on their behalf", async () => {
    const fetchMock = setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: BRAM._id } });
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Stofzuigen' }));

    expect(screen.getByRole('alertdialog')).toHaveTextContent('Wie heeft “Stofzuigen” gedaan?');
    expect(screen.getByRole('alertdialog')).toHaveTextContent('Deze taak staat op naam van Bram de Vries');
    fireEvent.click(screen.getByRole('button', { name: 'Namens Bram de Vries afvinken' }));

    await waitFor(() => expect(db.find((o) => o.id === 'o-other')?.completedBy).toBe(BRAM._id));
    expect(intentBodies(fetchMock, 'o-other', 'complete')).toEqual([{ completedBy: BRAM._id, takeOver: null }]);
    expect(await inSection('Afgerond', 'Gedaan door Bram de Vries')).toBeInTheDocument();
  });

  it('only offers to take the task over when its assignee is no longer active', async () => {
    const fetchMock = setup(makeSettings(), [ANNA, { ...BRAM, active: false }]);
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: 'all' } });
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Stofzuigen' }));

    const dialog = screen.getByRole('alertdialog');
    expect(dialog).toHaveTextContent('Bram de Vries, die niet meer actief is');
    expect(screen.queryByRole('button', { name: 'Namens Bram de Vries afvinken' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Ik heb de taak overgenomen' }));
    await waitFor(() => expect(intentBodies(fetchMock, 'o-other', 'complete')).toEqual([{ completedBy: null, takeOver: true }]));
  });

  it("can take over another person's task while checking it off", async () => {
    const fetchMock = setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: 'all' } });
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Stofzuigen' }));
    fireEvent.click(screen.getByRole('button', { name: 'Ik heb de taak overgenomen' }));

    await waitFor(() => {
      expect(db.find((o) => o.id === 'o-other')).toMatchObject({
        status: 'done',
        assigneeId: ANNA._id,
        completedBy: ANNA._id,
      });
    });
    expect(intentBodies(fetchMock, 'o-other', 'complete')).toEqual([{ completedBy: null, takeOver: true }]);
  });

  it('undo after skip → done restores skipped, also later via the item', async () => {
    setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Meer voor Badkamer' }));
    fireEvent.click(screen.getByRole('button', { name: 'Overslaan' }));
    fireEvent.change(screen.getByLabelText('Reden (optioneel)'), { target: { value: 'geen tijd' } });
    fireEvent.click(screen.getByRole('button', { name: 'Overslaan bevestigen' }));
    await waitFor(() => expect(db.find((o) => o.id === 'o-mine')?.status).toBe('skipped'));
    expect(await inSection('Afgerond', 'Overgeslagen: geen tijd')).toBeInTheDocument();

    // the skipped item gets completed elsewhere; any refetch picks that up
    db = db.map((o) => (o.id === 'o-mine' ? applyOptimistic(o, { id: o.id, kind: 'complete' }, { profileId: ANNA._id, todayKey: TODAY, now: NOW }) : o));
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Afvinken: Ramen' }));
    });
    const doneRow = (await inSection('Afgerond', 'Gedaan door Anna')).closest('li')!;
    fireEvent.click(within(doneRow).getByRole('button', { name: 'Badkamer ongedaan maken' }));
    await waitFor(() => expect(db.find((o) => o.id === 'o-mine')?.status).toBe('skipped'));
    expect(await inSection('Afgerond', 'Overgeslagen: geen tijd')).toBeInTheDocument();
  });

  it('opens the Extra Task dialog, records an extra execution for today and offers undo as a retract', async () => {
    const fetchMock = setup();
    renderWithProviders(<TodayPage now={NOW} />);
    // Recorded work is dated today, also when another day is being looked at.
    fireEvent.click(await screen.findByRole('button', { name: 'Morgen' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Extra taak' }));

    const dialog = await screen.findByRole('dialog', { name: 'Extra taak' });
    expect(within(dialog).getByRole('radio', { name: 'Extra keer voor een bestaande taak' })).toBeChecked();
    expect(within(dialog).getByRole('radio', { name: 'Eenmalige taak (komt niet in de takenlijst)' })).toBeInTheDocument();
    await waitFor(() => expect(within(within(dialog).getByLabelText('Taak')).getAllByRole('option')).toHaveLength(2));
    fireEvent.change(within(dialog).getByLabelText('Taak', { selector: 'select' }), { target: { value: 't2' } });
    // Badkamer is still open in today's plan: checking that off is the default, an extra one is the alternative.
    fireEvent.click(await within(dialog).findByRole('radio', { name: 'Toch een extra keer registreren' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Vastleggen' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const posted = fetchMock.mock.calls
      .filter(([u, init]) => u === '/api/v2/occurrences' && init?.method === 'POST')
      .map(([, init]) => JSON.parse(String(init!.body)));
    expect(posted).toEqual([
      { taskId: 't2', date: TODAY, assigneeId: ANNA._id, done: true, requestId: expect.stringMatching(/^[A-Za-z0-9_-]{16,64}$/) },
    ]);

    // The page jumped back to today and shows the record as finished, marked as extra.
    expect(screen.getByRole('button', { name: 'Vandaag' })).toHaveAttribute('aria-pressed', 'true');
    const row = (await inSection('Afgerond', 'Gedaan door Anna')).closest('li')!;
    expect(within(row).getByText('Extra')).toBeInTheDocument();
    const snackbar = screen.getByRole('status');
    expect(snackbar).toHaveTextContent('"Badkamer" is vastgelegd.');

    const refetchesBefore = (url: string) => fetchMock.mock.calls.filter(([u]) => u === url).length;
    const [tasksBefore] = [refetchesBefore('/api/tasks')];
    fireEvent.click(within(snackbar).getByRole('button', { name: 'Ongedaan maken' }));
    await waitFor(() => expect(db.some((o) => o.id === 'o-recorded')).toBe(false));
    // The retract also refreshes what the deleted work had fed: the tasks (lastCompletedAt).
    await waitFor(() => expect(refetchesBefore('/api/tasks')).toBeGreaterThan(tasksBefore));
    expect(intentBodies(fetchMock, 'o-recorded', 'complete')).toEqual([]);
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Afgerond' })).not.toBeInTheDocument());
  });

  it('checks off the planned task from the dialog and offers undo as an uncomplete', async () => {
    const fetchMock = setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Extra taak' }));
    const dialog = await screen.findByRole('dialog', { name: 'Extra taak' });
    await waitFor(() => expect(within(within(dialog).getByLabelText('Taak', { selector: 'select' })).getAllByRole('option')).toHaveLength(2));
    fireEvent.change(within(dialog).getByLabelText('Taak', { selector: 'select' }), { target: { value: 't2' } });
    fireEvent.click(await within(dialog).findByRole('button', { name: 'Afvinken' }));

    await waitFor(() => expect(db.find((o) => o.id === 'o-mine')?.status).toBe('done'));
    expect(intentBodies(fetchMock, 'o-mine', 'complete')).toEqual([{ completedBy: ANNA._id, takeOver: null }]);
    expect(fetchMock.mock.calls.filter(([u, init]) => u === '/api/v2/occurrences' && init?.method === 'POST')).toEqual([]);
    const snackbar = await screen.findByRole('status');
    expect(snackbar).toHaveTextContent('"Badkamer" afgevinkt.');
    fireEvent.click(within(snackbar).getByRole('button', { name: 'Ongedaan maken' }));
    await waitFor(() => expect(db.find((o) => o.id === 'o-mine')?.status).toBe('open'));
  });

  it('marks recorded extra work with an Extra badge, and its undo retracts instead of uncompleting', async () => {
    storeProfile(ANNA._id);
    db = [
      makeOccurrenceV2({
        id: 'o-extra',
        taskId: 't2',
        taskNameSnapshot: 'Badkamer',
        date: TODAY,
        assigneeId: ANNA._id,
        status: 'done',
        completedBy: ANNA._id,
        completedAt: NOW.toISOString(),
        origin: 'adhoc',
        recordedDone: true,
      }),
      makeOccurrenceV2({ id: 'o-plain', taskId: 't3', taskNameSnapshot: 'Ramen', date: TODAY, assigneeId: ANNA._id, status: 'done', completedBy: ANNA._id }),
    ];
    const fetchMock = mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings(),
      '/api/rooms': [makeRoom({ _id: 'r1', name: 'Badkamer-ruimte' })],
      '/api/tasks': [makeTask({ _id: 't2', name: 'Badkamer', roomId: 'r1' })],
      ...v2Basics(),
      '/api/v2/occurrences': () => page(db),
      'POST /api/v2/occurrences/o-extra/retraction': () => {
        db = db.filter((o) => o.id !== 'o-extra');
        return { retracted: true, id: 'o-extra' };
      },
    });
    renderWithProviders(<TodayPage now={NOW} />);

    const finished = await screen.findByRole('region', { name: 'Afgerond' });
    const rows = within(finished).getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByText('Extra')).toBeInTheDocument();
    expect(within(rows[1]!).queryByText('Extra')).not.toBeInTheDocument();

    fireEvent.click(within(rows[0]!).getByRole('button', { name: 'Badkamer ongedaan maken' }));
    await waitFor(() => expect(within(screen.getByRole('region', { name: 'Afgerond' })).queryByText('Extra')).not.toBeInTheDocument());
    expect(intentBodies(fetchMock, 'o-extra', 'uncomplete')).toEqual([]);
    expect(intentBodies(fetchMock, 'o-extra', 'retraction')).toHaveLength(1);
    expect(db.map((o) => o.id)).toEqual(['o-plain']);
  });

  it('shows a one-off task (no task record) from its snapshots', async () => {
    storeProfile(ANNA._id);
    db = [
      makeOccurrenceV2({
        id: 'o-oneoff',
        taskId: null,
        taskNameSnapshot: 'Gordijnen ophangen',
        roomIdSnapshot: 'r1',
        roomNameSnapshot: 'Woonkamer',
        date: TODAY,
        assigneeId: ANNA._id,
        origin: 'adhoc',
      }),
      makeOccurrenceV2({ id: 'o-roomless', taskId: null, taskNameSnapshot: 'Kast ophalen', date: TODAY, assigneeId: ANNA._id, origin: 'adhoc' }),
    ];
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings(),
      '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
      '/api/tasks': [],
      ...v2Basics(),
      '/api/v2/occurrences': () => page(db),
    });
    renderWithProviders(<TodayPage now={NOW} />);

    expect(await screen.findByText('Gordijnen ophangen')).toBeInTheDocument();
    expect(screen.getByText(/Woonkamer/)).toBeInTheDocument();
    expect(screen.getByText('Kast ophalen')).toBeInTheDocument();
  });

  it('treats a second retract (404) as already undone', async () => {
    storeProfile(ANNA._id);
    db = [
      makeOccurrenceV2({ id: 'o-extra', taskId: 't2', taskNameSnapshot: 'Badkamer', date: TODAY, assigneeId: ANNA._id, status: 'done', completedBy: ANNA._id, origin: 'adhoc', recordedDone: true }),
    ];
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings(),
      '/api/rooms': [],
      '/api/tasks': [],
      ...v2Basics(),
      '/api/v2/occurrences': () => page(db),
      // The record is gone already: the mock has no retract route and answers 404.
    });
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Badkamer ongedaan maken' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Badkamer ongedaan maken' })).toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('claims an unclaimed item', async () => {
    setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: 'all' } });
    fireEvent.click(await screen.findByRole('button', { name: 'Wastafel oppakken' }));
    expect(await inSection('Mijn taken', 'Wastafel')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Nog niet opgepakt' })).not.toBeInTheDocument();
  });

  it('reassigns an open task to another person and back to together', async () => {
    const fetchMock = setup();
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.change(await screen.findByLabelText('Filter op persoon'), { target: { value: 'all' } });
    fireEvent.click(screen.getByRole('button', { name: 'Meer voor Badkamer' }));

    fireEvent.change(screen.getByLabelText('Toewijzen aan'), { target: { value: BRAM._id } });
    await waitFor(() => expect(db.find((o) => o.id === 'o-mine')?.assigneeId).toBe(BRAM._id));

    fireEvent.click(screen.getByRole('button', { name: 'Meer voor Badkamer' }));
    fireEvent.change(screen.getByLabelText('Toewijzen aan'), { target: { value: '' } });
    await waitFor(() => expect(db.find((o) => o.id === 'o-mine')?.assigneeId).toBeNull());
    expect(intentBodies(fetchMock, 'o-mine', 'assignment')).toEqual([{ assigneeId: BRAM._id }, { assigneeId: null }]);
  });

  it('rolls back an optimistic check-off when the server fails', async () => {
    setup();
    failNext = true;
    renderWithProviders(<TodayPage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Afvinken: Badkamer' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Dat lukte niet. De wijziging is teruggedraaid.');
    expect(await inSection('Mijn taken', 'Badkamer')).toBeInTheDocument();
    expect(db.find((o) => o.id === 'o-mine')?.status).toBe('open');
  });

  it('uses large tap targets for checking off', async () => {
    setup();
    renderWithProviders(<TodayPage now={NOW} />);
    const button = await screen.findByRole('button', { name: 'Afvinken: Badkamer' });
    expect(button).toHaveClass('check-button');
  });
});
