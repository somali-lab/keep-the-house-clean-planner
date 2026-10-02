import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeRoom, makeSettings, makeTask, renderWithProviders } from '../../test/render.tsx';
import type { DueItemView } from './api.ts';
import { DuePage, spokenDate } from './DuePage.tsx';

const NOW = new Date('2026-09-16T08:00:00Z'); // Wednesday

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
    '/api/due': due,
    'POST /api/occurrences': { _id: 'new1', taskNameSnapshot: 'Stofzuigen' },
    '/api/tasks': [makeTask({ _id: 't2', name: 'Stofzuigen', roomId: 'r1' }), makeTask({ _id: 't4', name: 'Afwas', roomId: 'r1' })],
    '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
    'PATCH /api/occurrences/new1': { _id: 'new1', status: 'done' },
    'PATCH /api/occurrences/o-today': { _id: 'o-today', status: 'done' },
  });
}

function setupWithSettings(settings: ReturnType<typeof makeSettings>) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': settings,
    '/api/due': DUE,
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
      expect(callsTo(fetchMock, 'POST', '/api/occurrences')).toEqual([{ taskId: 't2', date: '2026-09-20', assigneeId: BRAM._id }]),
    );
    await waitFor(() => expect(screen.queryByRole('form')).not.toBeInTheDocument());
  });

  it('"Nu gedaan" records the execution in one request, already done, with an idempotency key', async () => {
    const fetchMock = setup();
    renderWithProviders(<DuePage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Stofzuigen nu gedaan' }));
    await waitFor(() => expect(callsTo(fetchMock, 'POST', '/api/occurrences')).toHaveLength(1));
    expect(callsTo(fetchMock, 'POST', '/api/occurrences')).toEqual([
      { taskId: 't2', date: '2026-09-16', done: true, requestId: expect.stringMatching(/^[A-Za-z0-9_-]{16,64}$/) },
    ]);
    expect(fetchMock.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'PATCH')).toEqual([]);
  });

  it('keeps the key when the same click is retried after a failure, and uses a new one after it succeeded', async () => {
    let attempts = 0;
    const fetchMock = mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings(),
      '/api/due': DUE,
      'POST /api/occurrences': () => {
        attempts += 1;
        if (attempts === 1) throw new TypeError('network down');
        return { _id: `new${attempts}` };
      },
    });
    storeProfile(ANNA._id);
    renderWithProviders(<DuePage now={NOW} />);
    const button = await screen.findByRole('button', { name: 'Stofzuigen nu gedaan' });
    fireEvent.click(button);
    expect(await screen.findByRole('alert')).toHaveTextContent('Dat lukte niet');
    await waitFor(() => expect(button).toBeEnabled());
    fireEvent.click(button);
    await waitFor(() => expect(callsTo(fetchMock, 'POST', '/api/occurrences')).toHaveLength(2));
    const [first, second] = callsTo(fetchMock, 'POST', '/api/occurrences') as { requestId: string }[];
    expect(second!.requestId).toBe(first!.requestId);

    // A deliberate second execution after the first one succeeded is a new intent.
    await waitFor(() => expect(button).toBeEnabled());
    fireEvent.click(button);
    await waitFor(() => expect(callsTo(fetchMock, 'POST', '/api/occurrences')).toHaveLength(3));
    expect((callsTo(fetchMock, 'POST', '/api/occurrences')[2] as { requestId: string }).requestId).not.toBe(first!.requestId);
  });

  it('offers an extra execution per task, opens the dialog on that task and records it as done today', async () => {
    const fetchMock = setup();
    renderWithProviders(<DuePage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Extra keer voor Stofzuigen vastleggen' }));

    const dialog = await screen.findByRole('dialog', { name: 'Gedaan werk vastleggen' });
    const task = within(dialog).getByLabelText('Taak');
    await waitFor(() => expect(task).toHaveValue('t2'));
    await waitFor(() => expect(within(dialog).getByLabelText('Gedaan door')).toHaveValue(ANNA._id));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Vastleggen' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(callsTo(fetchMock, 'POST', '/api/occurrences')).toEqual([
      { taskId: 't2', date: '2026-09-16', assigneeId: ANNA._id, done: true, requestId: expect.stringMatching(/^[A-Za-z0-9_-]{16,64}$/) },
    ]);
    expect(await screen.findByRole('status')).toHaveTextContent('"Stofzuigen" is vastgelegd.');
    // The due list is refreshed, because the extra execution restarts the clock of the task.
    await waitFor(() => expect(fetchMock.mock.calls.filter(([u]) => u === '/api/due').length).toBeGreaterThan(1));
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
    await waitFor(() => expect(callsTo(fetchMock, 'PATCH', '/api/occurrences/o-today')).toEqual([{ action: 'complete' }]));
    expect(callsTo(fetchMock, 'POST', '/api/occurrences')).toEqual([]);
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
