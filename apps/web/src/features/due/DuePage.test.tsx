import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
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
    'POST /api/occurrences': { _id: 'new1' },
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

  it('"Nu gedaan" adds an occurrence for today and completes it', async () => {
    const fetchMock = setup();
    renderWithProviders(<DuePage now={NOW} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Stofzuigen nu gedaan' }));
    await waitFor(() => expect(callsTo(fetchMock, 'PATCH', '/api/occurrences/new1')).toEqual([{ action: 'complete' }]));
    expect(callsTo(fetchMock, 'POST', '/api/occurrences')).toEqual([{ taskId: 't2', date: '2026-09-16', assigneeId: ANNA._id }]);
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
