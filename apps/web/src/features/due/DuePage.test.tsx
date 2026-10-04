import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { resetRequestKeys } from '../../api/requestKey.ts';
import { applyLanguage } from '../../i18n/runtime.ts';
import { ANNA, BRAM, mockApi, page, problem, storeProfile, v2Basics } from '../../test/fixtures.ts';
import { makeOccurrenceV2, makeRoomV2, makeSettings, makeTaskV2, renderWithProviders } from '../../test/render.tsx';
import type { DueItemView } from './api.ts';
import { DuePage, spokenDate } from './DuePage.tsx';

/** `GET /api/v2/due` as the server answers it: a bounded page with the summary of the whole list. */
const dueList = (items: DueItemView[]) => ({
  today: '2026-09-16',
  items,
  nextCursor: null,
  summary: { due: items.filter((i) => i.state === 'due').length, overdue: items.filter((i) => i.state === 'overdue').length },
});

const NOW = new Date('2026-09-16T08:00:00Z'); // Wednesday

afterEach(() => {
  resetRequestKeys();
  applyLanguage('nl');
});

const item = (overrides: Partial<DueItemView> & Pick<DueItemView, 'taskId' | 'taskName' | 'state'>): DueItemView => ({
  roomId: 'r1',
  roomName: 'Badkamer',
  intervalKey: '1w',
  intervalLabel: '1x per week',
  periodDays: 7,
  daysSince: 7,
  ratio: 1,
  lastCompletedAt: null,
  initialDueDate: '2026-09-09',
  nextOccurrence: null,
  ...overrides,
});

const DUE: DueItemView[] = [
  item({ taskId: 't1', taskName: 'Badkamer schoonmaken', state: 'overdue', daysSince: 84, ratio: 12, nextOccurrence: { id: 'o-sat', date: '2026-09-19', assigneeId: ANNA._id } }),
  item({ taskId: 't2', taskName: 'Stofzuigen', state: 'due', roomName: 'Woonkamer', daysSince: 7 }),
  item({ taskId: 't4', taskName: 'Afwas', state: 'due', intervalKey: 'daily', intervalLabel: 'Dagelijks', daysSince: 1, nextOccurrence: { id: 'o-today', date: '2026-09-16', assigneeId: null } }),
  item({ taskId: 't3', taskName: 'Ramen lappen', state: 'ok', daysSince: 3, ratio: 0.03 }),
];

function setup(due: DueItemView[] = DUE) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': makeSettings(),
    '/api/v2/due': dueList(due),
    '/api/v2/tasks': page([makeTaskV2({ id: 't2', name: 'Stofzuigen', roomId: 'r1' }), makeTaskV2({ id: 't4', name: 'Afwas', roomId: 'r1' })]),
    '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Woonkamer' })]),
    ...v2Basics(),
    '/api/v2/occurrences': page([]),
    'POST /api/v2/occurrences': makeOccurrenceV2({ id: 'new2', taskId: 't2', taskNameSnapshot: 'Stofzuigen', origin: 'adhoc', recordedDone: true }),
    'POST /api/v2/occurrences/o-today/complete': makeOccurrenceV2({ id: 'o-today', status: 'done' }),
  });
}

function setupWithSettings(settings: ReturnType<typeof makeSettings>) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': settings,
    '/api/v2/due': dueList(DUE),
  });
}

const callsTo = (fetchMock: ReturnType<typeof mockApi>, method: string, url: string) =>
  fetchMock.mock.calls
    .filter(([u, init]) => u === url && (init as RequestInit | undefined)?.method === method)
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)));

describe('DuePage', () => {
  it('lists due and overdue tasks in rank order, overdue marked with icon and label', async () => {
    setup();
    renderWithProviders(<DuePage now={NOW} />);
    const rows = await screen.findAllByRole('listitem');
    expect(rows).toHaveLength(3);
    expect(rows[0]).toHaveTextContent('Badkamer schoonmaken');
    expect(rows[0]).toHaveTextContent('⚠ Flink achter');
    expect(rows[0]).toHaveTextContent('Badkamer · 1x per week · Eerste keer aan de beurt op wo 9 sep');
    expect(rows[1]).toHaveTextContent('Stofzuigen');
    expect(rows[1]).toHaveTextContent('Aan de beurt');
    expect(rows[1]).not.toHaveTextContent('Flink achter');
    expect(screen.queryByText('Ramen lappen')).not.toBeInTheDocument();
  });

  it('reads every page of the due list and maps the numbers the server may send as strings', async () => {
    storeProfile(ANNA._id);
    mockApi({
      '/api/v2/due': (_init: RequestInit | undefined, url: string) =>
        new URL(url, 'http://localhost').searchParams.get('cursor') === 'p2'
          ? { ...dueList([DUE[1]!]), nextCursor: null }
          : { ...dueList([{ ...DUE[0]!, lastCompletedAt: '2026-06-24T10:00:00Z', daysSince: '84', ratio: '12.5', periodDays: '7' } as unknown as DueItemView]), nextCursor: 'p2' },
      '/api/settings': makeSettings(),
      '/api/users': [ANNA, BRAM],
      '/api/v2/tasks': page([]),
      '/api/v2/rooms': page([]),
      ...v2Basics(),
    });
    renderWithProviders(<DuePage now={NOW} />);
    const rows = await screen.findAllByRole('listitem');
    expect(rows.map((row) => row.textContent)).toEqual([
      expect.stringContaining('Badkamer schoonmaken'),
      expect.stringContaining('Stofzuigen'),
    ]);
    expect(rows[0]).toHaveTextContent('84 dagen geleden');
  });

  it('explains why a task is both on a day and in the due list', async () => {
    setup();
    renderWithProviders(<DuePage now={NOW} />);
    const [first] = await screen.findAllByRole('listitem');
    expect(first).toHaveTextContent('Staat nog open op za 19 sep.');
    expect(screen.getByText('Een taak verschijnt vanaf de eerste geplande datum; daarna telt wanneer die echt gedaan is.')).toBeInTheDocument();
  });

  it('plans a task on a chosen day and person', async () => {
    const fetchMock = setup();
    renderWithProviders(<DuePage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Stofzuigen inplannen' }));
    const form = screen.getByRole('form', { name: 'Stofzuigen inplannen' });
    fireEvent.change(within(form).getByLabelText('Datum'), { target: { value: '2026-09-20' } });
    fireEvent.change(within(form).getByLabelText('Wie'), { target: { value: BRAM._id } });
    fireEvent.click(within(form).getByRole('button', { name: 'Inplannen bevestigen' }));

    await waitFor(() =>
      expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toEqual([{ taskId: 't2', date: '2026-09-20', assigneeId: BRAM._id }]),
    );
    await waitFor(() => expect(screen.queryByRole('form')).not.toBeInTheDocument());
  });

  it('plans for anyone with an explicit null assignee, which the API documents as "anyone"', async () => {
    const fetchMock = setup();
    renderWithProviders(<DuePage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Stofzuigen inplannen' }));
    const form = screen.getByRole('form', { name: 'Stofzuigen inplannen' });
    fireEvent.click(within(form).getByRole('button', { name: 'Inplannen bevestigen' }));
    await waitFor(() =>
      expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toEqual([{ taskId: 't2', date: '2026-09-16', assigneeId: null }]),
    );
  });

  it('shows a problem of the server as the action error', async () => {
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings(),
      '/api/v2/due': dueList(DUE),
      'POST /api/v2/occurrences': () => problem(409, 'cycle_not_generated', 'No cycle', { date: '2026-12-01' }),
      '/api/v2/tasks': page([]),
      '/api/v2/rooms': page([]),
      ...v2Basics(),
    });
    storeProfile(ANNA._id);
    renderWithProviders(<DuePage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Stofzuigen nu gedaan' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Dat lukte niet');
  });

  it('"Nu gedaan" records the execution in one request, already done, with an idempotency key', async () => {
    const fetchMock = setup();
    renderWithProviders(<DuePage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Stofzuigen nu gedaan' }));
    await waitFor(() => expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toHaveLength(1));
    expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toEqual([
      { taskId: 't2', date: '2026-09-16', done: true, requestId: expect.stringMatching(/^[A-Za-z0-9_-]{16,64}$/) },
    ]);
    expect(fetchMock.mock.calls.filter(([u]) => String(u).endsWith('/complete'))).toEqual([]);
  });

  it('keeps the key when the same click is retried after a failure, and uses a new one after it succeeded', async () => {
    let attempts = 0;
    const fetchMock = mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings(),
      '/api/v2/due': dueList(DUE),
      'POST /api/v2/occurrences': () => {
        attempts += 1;
        if (attempts === 1) throw new TypeError('network down');
        return makeOccurrenceV2({ id: `new${attempts}` });
      },
    });
    storeProfile(ANNA._id);
    renderWithProviders(<DuePage now={NOW} />);
    const button = await screen.findByRole('button', { name: 'Stofzuigen nu gedaan' });
    fireEvent.click(button);
    expect(await screen.findByRole('alert')).toHaveTextContent('Dat lukte niet');
    await waitFor(() => expect(button).toBeEnabled());
    fireEvent.click(button);
    await waitFor(() => expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toHaveLength(2));
    const [first, second] = callsTo(fetchMock, 'POST', '/api/v2/occurrences') as { requestId: string }[];
    expect(second!.requestId).toBe(first!.requestId);

    // A deliberate second execution after the first one succeeded is a new intent.
    await waitFor(() => expect(button).toBeEnabled());
    fireEvent.click(button);
    await waitFor(() => expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toHaveLength(3));
    expect((callsTo(fetchMock, 'POST', '/api/v2/occurrences')[2] as { requestId: string }).requestId).not.toBe(first!.requestId);
  });

  it('offers an extra execution per task, opens the dialog on that task and records it as done today', async () => {
    const fetchMock = setup();
    renderWithProviders(<DuePage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Extra keer voor Stofzuigen vastleggen' }));

    const dialog = await screen.findByRole('dialog', { name: 'Extra taak' });
    const task = within(dialog).getByLabelText('Taak');
    await waitFor(() => expect(task).toHaveValue('t2'));
    await waitFor(() => expect(within(dialog).getByLabelText('Gedaan door')).toHaveValue(ANNA._id));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Vastleggen' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toEqual([
      { taskId: 't2', date: '2026-09-16', assigneeId: ANNA._id, done: true, requestId: expect.stringMatching(/^[A-Za-z0-9_-]{16,64}$/) },
    ]);
    expect(await screen.findByRole('status')).toHaveTextContent('"Stofzuigen" is vastgelegd.');
    // The due list is refreshed, because the extra execution restarts the clock of the task.
    await waitFor(() => expect(fetchMock.mock.calls.filter(([u]) => String(u).startsWith('/api/v2/due')).length).toBeGreaterThan(1));
  });

  it('uses the configured control for completing a due task', async () => {
    setupWithSettings(makeSettings({ completionControl: 'thumb' }));
    renderWithProviders(<DuePage now={NOW} />);
    const button = await screen.findByRole('button', { name: 'Stofzuigen nu gedaan' });
    expect(button.querySelector('.lucide-thumbs-up')).not.toBeNull();
  });

  it('"Nu gedaan" completes today\'s planned occurrence instead of adding one', async () => {
    const fetchMock = setup();
    renderWithProviders(<DuePage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Afwas nu gedaan' }));
    await waitFor(() => expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences/o-today/complete')).toEqual([{}]));
    expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toEqual([]);
  });

  describe('"Nu gedaan" for work that is planned today for someone else', () => {
    const SOMEONE_ELSES: DueItemView[] = [
      item({
        taskId: 't5',
        taskName: 'Vaatwasser leegmaken',
        state: 'due',
        nextOccurrence: { id: 'o-bram', date: '2026-09-16', assigneeId: BRAM._id },
      }),
      item({
        taskId: 't6',
        taskName: 'Planten water geven',
        state: 'due',
        nextOccurrence: { id: 'o-anna', date: '2026-09-16', assigneeId: ANNA._id },
      }),
      item({
        taskId: 't7',
        taskName: 'Kattenbak verschonen',
        state: 'due',
        nextOccurrence: { id: 'o-bram-later', date: '2026-09-19', assigneeId: BRAM._id },
      }),
    ];
    const setupOthers = () => {
      storeProfile(ANNA._id);
      return mockApi({
        '/api/users': [ANNA, BRAM],
        '/api/settings': makeSettings(),
        '/api/v2/due': dueList(SOMEONE_ELSES),
        'POST /api/v2/occurrences': makeOccurrenceV2({ id: 'new1', taskNameSnapshot: 'Kattenbak verschonen' }),
        'POST /api/v2/occurrences/o-bram/complete': makeOccurrenceV2({ id: 'o-bram', status: 'done' }),
        'POST /api/v2/occurrences/o-anna/complete': makeOccurrenceV2({ id: 'o-anna', status: 'done' }),
      });
    };

    it('asks who performed it and says who receives the points, without sending anything yet', async () => {
      const fetchMock = setupOthers();
      renderWithProviders(<DuePage now={NOW} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Vaatwasser leegmaken nu gedaan' }));

      const dialog = await screen.findByRole('alertdialog');
      expect(dialog).toHaveTextContent('Wie heeft “Vaatwasser leegmaken” gedaan?');
      expect(dialog).toHaveTextContent('Deze taak staat op naam van Bram de Vries');
      expect(dialog).toHaveTextContent('namens Bram de Vries afvinken geeft Bram de Vries de punten');
      expect(dialog).toHaveTextContent('zelf overnemen geeft jou de punten');
      expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences/o-bram/complete')).toEqual([]);
      expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toEqual([]);
    });

    it('checks it off on behalf of the assignee, who then receives the points', async () => {
      const fetchMock = setupOthers();
      renderWithProviders(<DuePage now={NOW} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Vaatwasser leegmaken nu gedaan' }));
      fireEvent.click(await screen.findByRole('button', { name: 'Namens Bram de Vries afvinken' }));
      await waitFor(() =>
        expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences/o-bram/complete')).toEqual([
          { completedBy: BRAM._id },
        ]),
      );
      expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    });

    it('only offers to take the task over when the assignee is no longer active', async () => {
      storeProfile(ANNA._id);
      const fetchMock = mockApi({
        '/api/users': [ANNA, { ...BRAM, active: false }],
        '/api/settings': makeSettings(),
        '/api/v2/due': dueList(SOMEONE_ELSES),
        'POST /api/v2/occurrences/o-bram/complete': makeOccurrenceV2({ id: 'o-bram', status: 'done' }),
      });
      renderWithProviders(<DuePage now={NOW} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Vaatwasser leegmaken nu gedaan' }));
      const dialog = await screen.findByRole('alertdialog');
      expect(dialog).toHaveTextContent('Bram de Vries, die niet meer actief is');
      expect(screen.queryByRole('button', { name: 'Namens Bram de Vries afvinken' })).not.toBeInTheDocument();
      fireEvent.click(screen.getByRole('button', { name: 'Ik heb de taak overgenomen' }));
      await waitFor(() => expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences/o-bram/complete')).toEqual([{ takeOver: true }]));
    });

    it('takes the task over, so the actor receives the points', async () => {
      const fetchMock = setupOthers();
      renderWithProviders(<DuePage now={NOW} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Vaatwasser leegmaken nu gedaan' }));
      fireEvent.click(await screen.findByRole('button', { name: 'Ik heb de taak overgenomen' }));
      await waitFor(() =>
        expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences/o-bram/complete')).toEqual([{ takeOver: true }]),
      );
    });

    it('sends nothing when the dialog is cancelled', async () => {
      const fetchMock = setupOthers();
      renderWithProviders(<DuePage now={NOW} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Vaatwasser leegmaken nu gedaan' }));
      fireEvent.click(await screen.findByRole('button', { name: 'Annuleren' }));
      await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
      expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences/o-bram/complete')).toEqual([]);
      expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toEqual([]);
    });

    it('does not ask for work of the actor, or when the occurrence of someone else is on another day', async () => {
      const fetchMock = setupOthers();
      renderWithProviders(<DuePage now={NOW} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Planten water geven nu gedaan' }));
      await waitFor(() =>
        expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences/o-anna/complete')).toEqual([{}]),
      );
      expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();

      fireEvent.click(await screen.findByRole('button', { name: 'Kattenbak verschonen nu gedaan' }));
      await waitFor(() => expect(callsTo(fetchMock, 'POST', '/api/v2/occurrences')).toHaveLength(1));
      expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    });

    it('is worded in English too', async () => {
      applyLanguage('en');
      setupOthers();
      renderWithProviders(<DuePage now={NOW} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Vaatwasser leegmaken done now' }));
      const dialog = await screen.findByRole('alertdialog');
      expect(dialog).toHaveTextContent('Who completed “Vaatwasser leegmaken”?');
      expect(dialog).toHaveTextContent('checking it off for Bram de Vries gives Bram de Vries the points');
      expect(dialog).toHaveTextContent('taking it over gives you the points');
    });
  });

  it('says when nothing is due', async () => {
    setup([item({ taskId: 't3', taskName: 'Ramen lappen', state: 'ok', ratio: 0.1 })]);
    renderWithProviders(<DuePage now={NOW} />);
    expect(await screen.findByText('Geen achterstand: alles is bijgehouden.')).toBeInTheDocument();
  });

  it('formats dates the Dutch way', () => {
    expect(spokenDate('2026-09-19')).toBe('za 19 sep');
    expect(spokenDate('2026-12-01')).toBe('di 1 dec');
  });
});
