import type {
  CompletionResponse,
  DeviationsResponse,
  IntervalsResponse,
  PointsBalancesResponse,
  PointsEntriesResponse,
  WorkloadResponse,
} from '@huishoudplanner/shared';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, makeUser, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeRoom, makeSettings, makeTask, renderWithProviders } from '../../test/render.tsx';
import { StatsPage } from './StatsPage.tsx';

const week = (
  weekIndex: number,
  startDate: string,
  anna: [number, number],
  bram: [number, number],
  unassigned = 0,
) => ({
  weekIndex,
  startDate,
  users: [
    { userId: ANNA._id, plannedMinutes: anna[0], doneMinutes: anna[1] },
    { userId: BRAM._id, plannedMinutes: bram[0], doneMinutes: bram[1] },
  ],
  unassignedPlannedMinutes: unassigned,
});

const WORKLOAD: WorkloadResponse = {
  cycles: [
    {
      index: 0,
      startDate: '2026-09-14',
      endDate: '2026-10-11',
      users: [
        { userId: ANNA._id, plannedMinutes: 165, doneMinutes: 75 },
        { userId: BRAM._id, plannedMinutes: 40, doneMinutes: 70 },
      ],
      unassignedPlannedMinutes: 0,
      weeks: [
        week(0, '2026-09-14', [30, 30], [20, 20]),
        week(1, '2026-09-21', [75, 45], [0, 30]),
        week(2, '2026-09-28', [30, 0], [20, 20]),
        week(3, '2026-10-05', [30, 0], [0, 0]),
      ],
    },
    {
      index: 1,
      startDate: '2026-10-12',
      endDate: '2026-11-08',
      users: [
        { userId: ANNA._id, plannedMinutes: 120, doneMinutes: 30 },
        { userId: BRAM._id, plannedMinutes: 40, doneMinutes: 0 },
      ],
      unassignedPlannedMinutes: 45,
      weeks: [
        week(0, '2026-10-12', [30, 30], [20, 0]),
        week(1, '2026-10-19', [30, 0], [0, 0], 45),
        week(2, '2026-10-26', [30, 0], [20, 0]),
        week(3, '2026-11-02', [30, 0], [0, 0]),
      ],
    },
  ],
};

const COMPLETION = (groupBy: string): CompletionResponse =>
  groupBy === 'room'
    ? {
        groupBy: 'room',
        rows: [
          { key: 'r1', name: 'Badkamer', done: 3, skipped: 1, missed: 1, rate: 0.6 },
          { key: null, name: '', done: 1, skipped: 0, missed: 0, rate: 1 },
        ],
      }
    : {
        groupBy: 'task',
        rows: [
          { key: 't1', name: 'Badkamer schoonmaken', done: 3, skipped: 1, missed: 1, rate: 0.6 },
          { key: 't2', name: 'Keuken dweilen', done: 2, skipped: 0, missed: 0, rate: 1 },
          { key: null, name: '', done: 2, skipped: 0, missed: 0, rate: 1 },
        ],
      };

const INTERVALS: IntervalsResponse = {
  rows: [
    {
      taskId: 't1',
      name: 'Badkamer schoonmaken',
      intervalKey: '1w',
      periodDays: 7,
      completions: 3,
      averageDays: 14,
      deviation: 2,
    },
    {
      taskId: 't2',
      name: 'Keuken dweilen',
      intervalKey: '2wk',
      periodDays: 14,
      completions: 2,
      averageDays: 15,
      deviation: 15 / 14,
    },
    {
      taskId: 't4',
      name: 'Afwas',
      intervalKey: '1w',
      periodDays: 7,
      completions: 5,
      averageDays: 3.5,
      deviation: 0.5,
    },
    {
      taskId: 't3',
      name: 'Ramen lappen',
      intervalKey: '4wk',
      periodDays: 28,
      completions: 1,
      averageDays: null,
      deviation: null,
    },
  ],
};

const DEVIATIONS: DeviationsResponse = {
  rows: [
    {
      taskId: 't2',
      name: 'Keuken dweilen',
      completions: 4,
      averagePlanningShiftDays: 0,
      averageCompletionDelayDays: 1.25,
      early: 0,
      onTime: 2,
      late: 2,
    },
    {
      taskId: 't1',
      name: 'Badkamer schoonmaken',
      completions: 5,
      averagePlanningShiftDays: 1,
      averageCompletionDelayDays: 0,
      early: 0,
      onTime: 5,
      late: 0,
    },
    {
      taskId: 't3',
      name: 'Ramen lappen',
      completions: 1,
      averagePlanningShiftDays: 0,
      averageCompletionDelayDays: 0,
      early: 0,
      onTime: 1,
      late: 0,
    },
  ],
};

function setup(workload: WorkloadResponse = WORKLOAD, extraRoutes: Record<string, unknown> = {}) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': makeSettings(),
    '/api/rooms': [
      makeRoom({ _id: 'r1', name: 'Badkamer' }),
      makeRoom({ _id: 'r2', name: 'Keuken' }),
    ],
    '/api/tasks': [
      makeTask({ _id: 't1', name: 'Badkamer schoonmaken', roomId: 'r1' }),
      makeTask({ _id: 't2', name: 'Keuken dweilen', roomId: 'r2' }),
      makeTask({ _id: 't3', name: 'Ramen lappen', roomId: 'r1' }),
      makeTask({ _id: 't4', name: 'Afwas', roomId: 'r2' }),
    ],
    '/api/stats/workload': workload,
    '/api/stats/completion': (_init: RequestInit | undefined, url: string) =>
      COMPLETION(new URL(url, 'http://x').searchParams.get('groupBy') ?? 'task'),
    '/api/stats/intervals': INTERVALS,
    '/api/stats/deviations': DEVIATIONS,
    'DELETE /api/stats': {
      deletedOccurrences: 12,
      resetOccurrences: 4,
      resetTasks: 2,
      deletedPastCycles: 1,
    },
    ...extraRoutes,
  });
}

const statsUrls = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.map(([u]) => String(u)).filter((u) => u.startsWith('/api/stats/'));

async function selectStatsTab(name: string) {
  const tab = await screen.findByRole('tab', { name });
  fireEvent.mouseDown(tab, { button: 0, ctrlKey: false });
  fireEvent.click(tab);
  await waitFor(() => expect(tab).toHaveAttribute('aria-selected', 'true'));
}

describe('StatsPage', () => {
  it('shows planned vs done per person for the period, with legend and a table view', async () => {
    setup();
    renderWithProviders(<StatsPage />);
    const overviewTab = await screen.findByRole('tab', { name: 'Overzicht' });
    expect(overviewTab).toHaveAttribute('aria-selected', 'true');
    expect(overviewTab).toHaveClass('flex-none');
    expect(screen.getByRole('tablist', { name: 'Statistiekonderdeel' })).toHaveClass('overflow-x-auto', 'overflow-y-hidden', '[scrollbar-width:none]');
    await selectStatsTab('Eerlijkheid');
    const figure = await screen.findByRole('figure', { name: 'Gepland en gedaan per persoon' });

    const legend = within(figure).getByRole('list', { name: 'Legenda' });
    expect(
      within(legend)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(['Gepland', 'Gedaan']);
    expect(
      within(figure).getByRole('img', { name: 'Gepland en gedaan per persoon' }),
    ).toBeInTheDocument();
    // selective direct labels at the bar tips
    expect(within(figure).getByText('285 min')).toBeInTheDocument();

    fireEvent.click(within(figure).getByRole('button', { name: 'Toon als tabel' }));
    const rows = within(within(figure).getByRole('table')).getAllByRole('row').slice(1);
    expect(rows.map((r) => r.textContent)).toEqual([
      'Anna285 min105 min',
      'Bram de Vries80 min70 min',
    ]);
    expect(within(figure).getByRole('button', { name: 'Toon als grafiek' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
  });

  it('shows the value in a tooltip when a bar gets keyboard focus', async () => {
    setup();
    renderWithProviders(<StatsPage />);
    await selectStatsTab('Eerlijkheid');
    const figure = await screen.findByRole('figure', { name: 'Gepland en gedaan per persoon' });
    fireEvent.focus(within(figure).getByLabelText('Bram de Vries, gedaan: 70 min'));
    const tip = within(figure).getByRole('status');
    expect(tip).toHaveTextContent('70 min');
    expect(tip).toHaveTextContent('Bram de Vries · gedaan');
  });

  it('lists planned / done per week and the unassigned minutes', async () => {
    setup();
    renderWithProviders(<StatsPage />);
    await selectStatsTab('Eerlijkheid');
    const table = await screen.findByRole('table', {
      name: 'Per week (gepland / gedaan, in minuten)',
    });
    const rows = within(table).getAllByRole('row');
    expect(rows[2]).toHaveTextContent('Cyclus 1 · week 2 (21-09)75 / 450 / 300 min');
    expect(rows[6]).toHaveTextContent('Cyclus 2 · week 2 (19-10)30 / 00 / 045 min');
  });

  it('shows the workload trend per person with a crosshair tooltip listing every series', async () => {
    setup();
    renderWithProviders(<StatsPage />);
    await selectStatsTab('Werkbelasting door de tijd');
    const figure = await screen.findByRole('figure', { name: 'Gedaan per persoon per cyclus' });
    const chart = within(figure).getByRole('img', { name: 'Gedaan per persoon per cyclus' });
    expect(chart.parentElement).toHaveClass('max-w-4xl');
    expect(chart).toHaveClass('overflow-hidden');
    const legend = within(figure).getByRole('list', { name: 'Legenda' });
    expect(
      within(legend)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(['Anna', 'Bram de Vries']);

    fireEvent.focus(within(figure).getByLabelText(/^Cyclus 1 \(14-09\):/));
    const tip = within(figure).getByRole('status');
    expect(tip).toHaveTextContent('75 min');
    expect(tip).toHaveTextContent('70 min');

    fireEvent.click(within(figure).getByRole('button', { name: 'Toon als tabel' }));
    const rows = within(within(figure).getByRole('table')).getAllByRole('row');
    expect(rows[1]).toHaveTextContent(
      'Cyclus 1 (14-09)75 min (gepland 165 min)70 min (gepland 40 min)',
    );
  });

  it('shows completion rates and regroups on request', async () => {
    const fetchMock = setup();
    renderWithProviders(<StatsPage />);
    await selectStatsTab('Voltooiing');
    const heading = await screen.findByRole('heading', { name: 'Voltooiing' });
    const section = heading.closest('section')!;
    await waitFor(() =>
      expect(within(section).getByText('Badkamer schoonmaken')).toBeInTheDocument(),
    );
    const first = within(section).getAllByRole('row')[1]!;
    expect(first).toHaveTextContent('Badkamer schoonmakenBadkamer31160%');
    // One-off tasks have no task record: one combined row, labelled instead of unnamed.
    expect(within(section).getByRole('rowheader', { name: 'Eenmalige taak' })).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Voltooiing per'), { target: { value: 'room' } });
    await waitFor(() =>
      expect(statsUrls(fetchMock)).toContain('/api/stats/completion?weeks=1&groupBy=room'),
    );
    expect(
      await within(section).findByRole('columnheader', { name: 'Ruimte' }),
    ).toBeInTheDocument();
    expect(await within(section).findByRole('rowheader', { name: 'Zonder ruimte' })).toBeInTheDocument();
  });

  it('flags intervals that are wishful thinking with an icon and words, not colour', async () => {
    setup();
    renderWithProviders(<StatsPage />);
    await selectStatsTab('Intervallen: bedoeld en werkelijk');
    const heading = await screen.findByRole('heading', {
      name: 'Intervallen: bedoeld en werkelijk',
    });
    const section = heading.closest('section')!;
    await waitFor(() => expect(within(section).getAllByRole('row')).toHaveLength(5));
    const rows = within(section).getAllByRole('row').slice(1);
    expect(rows.map((r) => r.textContent)).toEqual([
      'Badkamer schoonmakenBadkamerelke 7 dagen14,0 dagen⚠ Gebeurt minder vaak dan bedoeld (×2,0)',
      'Keuken dweilenKeukenelke 14 dagen15,0 dagenOngeveer zoals bedoeld (×1,1)',
      'AfwasKeukenelke 7 dagen3,5 dagenGebeurt vaker dan bedoeld (×0,5)',
      'Ramen lappenBadkamerelke 28 dagen—Te weinig voltooiingen om te beoordelen',
    ]);
  });

  it('applies the period filter to every chart and table', async () => {
    const fetchMock = setup();
    const firstView = renderWithProviders(<StatsPage />, { headerReset: true });
    const period = await screen.findByLabelText('Periode');
    expect(screen.getByRole('button', { name: 'Filters van dit scherm resetten' })).toBeDisabled();
    expect(within(period).getByRole('option', { name: 'Laatste 13 cycli' })).toBeInTheDocument();
    fireEvent.change(period, { target: { value: 'weeks:3' } });
    await waitFor(() =>
      expect(statsUrls(fetchMock)).toEqual(
        expect.arrayContaining([
          '/api/stats/workload?weeks=3',
          '/api/stats/completion?weeks=3&groupBy=task',
          '/api/stats/intervals?weeks=3',
          '/api/stats/deviations?weeks=3',
        ]),
      ),
    );

    fireEvent.change(period, { target: { value: 'cycles:4' } });
    await waitFor(() =>
      expect(statsUrls(fetchMock)).toEqual(
        expect.arrayContaining([
          '/api/stats/workload?cycles=4',
          '/api/stats/completion?cycles=4&groupBy=task',
          '/api/stats/intervals?cycles=4',
          '/api/stats/deviations?cycles=4',
        ]),
      ),
    );
    firstView.unmount();
    renderWithProviders(<StatsPage />, { headerReset: true });
    expect(await screen.findByLabelText('Periode')).toHaveValue('cycles:4');
    fireEvent.click(screen.getByRole('button', { name: 'Filters van dit scherm resetten' }));
    expect(screen.getByLabelText('Periode')).toHaveValue('weeks:1');
  });

  it('shows planning shifts separately from completion delays and suggests improvements', async () => {
    setup();
    renderWithProviders(<StatsPage />);
    await selectStatsTab('Afwijking tussen planning en uitvoering');
    const heading = await screen.findByRole('heading', { name: 'Afwijking tussen planning en uitvoering' });
    const rows = within(heading.closest('section')!).getAllByRole('row').slice(1);
    expect(rows.map((row) => row.textContent)).toEqual([
      'Keuken dweilenKeuken40,0 dagen+1,3 dagenPlan deze taak mogelijk later',
      'Badkamer schoonmakenBadkamer5+1,0 dagen0,0 dagenDeze taak wordt vaak verplaatst; heroverweeg de vaste dag',
      'Ramen lappenBadkamer10,0 dagen0,0 dagenNog te weinig metingen voor een advies',
    ]);
  });

  it('explains when there is nothing to show yet', async () => {
    setup({ cycles: [] });
    renderWithProviders(<StatsPage />);
    expect(await screen.findByText(/Nog geen gegevens/)).toBeInTheDocument();
    expect(screen.queryByRole('figure')).not.toBeInTheDocument();
    await selectStatsTab('Eerlijkheid');
    expect(await screen.findByText(/Nog geen gegevens/)).toBeInTheDocument();
  });

  it('clears statistics only after explicit confirmation', async () => {
    const fetchMock = setup();
    renderWithProviders(<StatsPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Statistieken wissen' }));
    expect(screen.getByRole('heading', { name: 'Alle statistieken wissen?' })).toBeInTheDocument();
    expect(
      screen.getByText(/Personen, ruimtes, taken en de huidige planning blijven bestaan/),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Definitief opnieuw beginnen' }));

    expect(await screen.findByRole('status')).toHaveTextContent(
      'Je begint opnieuw met de bestaande planning.',
    );
    expect(
      fetchMock.mock.calls.some(
        ([url, init]) =>
          url === '/api/stats' && (init as RequestInit | undefined)?.method === 'DELETE',
      ),
    ).toBe(true);
  });

  it('purges only data before a chosen date, without a full reset', async () => {
    const fetchMock = setup();
    renderWithProviders(<StatsPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Oude data opschonen' }));
    expect(screen.getByRole('heading', { name: 'Oude data verwijderen?' })).toBeInTheDocument();

    const dateInput = screen.getByLabelText('Verwijder alles van vóór');
    fireEvent.change(dateInput, { target: { value: '2026-09-21' } });
    fireEvent.click(screen.getByRole('button', { name: 'Oude data verwijderen' }));

    expect(await screen.findByRole('status')).toHaveTextContent(
      'Oude data zijn opgeschoond. Alles vanaf de gekozen datum bleef staan.',
    );
    const deleteCall = fetchMock.mock.calls.find(
      ([url, init]) => String(url).startsWith('/api/stats') && (init as RequestInit | undefined)?.method === 'DELETE',
    );
    expect(deleteCall?.[0]).toBe('/api/stats?before=2026-09-21');
  });
});

describe('StatsPage: points', () => {
  const NOW = new Date('2026-09-16T08:00:00.000Z');
  const FORMER = makeUser({ _id: 'c00000000000000000000003', name: 'Carla', active: false });
  const BALANCES: PointsBalancesResponse = {
    from: '2026-09-14',
    to: '2026-09-20',
    balances: [
      { personId: ANNA._id, points: 8, executions: 3 },
      { personId: BRAM._id, points: 0, executions: 0 },
      { personId: FORMER._id, points: 4, executions: 1 },
    ],
  };
  const entry = (id: string, personId: string, date: string, amount: number, title: string) => ({
    _id: id,
    key: `execution:${id}`,
    kind: 'execution' as const,
    personId,
    amount,
    date,
    weekStart: '2026-09-14',
    occurrenceId: id,
    taskId: null,
    titleSnapshot: title,
    source: 'live' as const,
    createdAt: '2026-09-16T08:00:00.000Z',
    updatedAt: '2026-09-16T08:00:00.000Z',
  });
  const ENTRIES: Record<string, PointsEntriesResponse> = {
    [ANNA._id]: {
      entries: [
        entry('e00000000000000000000001', ANNA._id, '2026-09-16', 5, 'Ramen lappen'),
        entry('e00000000000000000000002', ANNA._id, '2026-09-14', 3, 'Stofzuigen'),
      ],
    },
    [BRAM._id]: { entries: [] },
    [FORMER._id]: { entries: [entry('e00000000000000000000003', FORMER._id, '2026-09-15', 4, 'Afwassen')] },
  };
  const pointsRoutes = (balances: PointsBalancesResponse = BALANCES) => ({
    '/api/users': [ANNA, BRAM, FORMER],
    '/api/points/balances': balances,
    '/api/points/entries': (_init: RequestInit | undefined, url: string) =>
      ENTRIES[new URL(url, 'http://x').searchParams.get('personId') ?? ''] ?? { entries: [] },
  });
  const pointsUrls = (fetchMock: ReturnType<typeof mockApi>) =>
    fetchMock.mock.calls.map(([u]) => String(u)).filter((u) => u.startsWith('/api/points/'));

  it('shows the balance of every person and the entries of the active profile for the selected period', async () => {
    const fetchMock = setup(WORKLOAD, pointsRoutes());
    renderWithProviders(<StatsPage now={NOW} />);
    await selectStatsTab('Punten');
    const heading = await screen.findByRole('heading', { name: 'Punten' });
    const section = heading.closest('section')!;
    expect(section).toHaveTextContent('14 september 2026 tot 20 september 2026');

    const balances = await within(section).findByRole('table', { name: 'Punten per persoon' });
    expect(within(balances).getAllByRole('row').slice(1).map((row) => row.textContent)).toEqual([
      'Anna83',
      'Bram de Vries00',
      'Carla (inactief)41',
    ]);
    const entries = await within(section).findByRole('table', { name: 'Posten van Anna' });
    expect(within(entries).getAllByRole('row').slice(1).map((row) => row.textContent)).toEqual([
      '16 september 2026Ramen lappen+5',
      '14 september 2026Stofzuigen+3',
    ]);
    expect(pointsUrls(fetchMock)).toEqual([
      '/api/points/balances?from=2026-09-14&to=2026-09-20',
      '/api/points/entries?personId=a00000000000000000000001&from=2026-09-14&to=2026-09-20',
    ]);
  });

  it('shows the entries of another person, also of someone who is inactive', async () => {
    setup(WORKLOAD, pointsRoutes());
    renderWithProviders(<StatsPage now={NOW} />);
    await selectStatsTab('Punten');
    const picker = await screen.findByLabelText('Toon posten van');
    fireEvent.change(picker, { target: { value: FORMER._id } });
    const table = await screen.findByRole('table', { name: 'Posten van Carla' });
    expect(within(table).getAllByRole('row').slice(1).map((row) => row.textContent)).toEqual(['15 september 2026Afwassen+4']);

    fireEvent.change(picker, { target: { value: BRAM._id } });
    expect(await screen.findByText('Bram de Vries heeft in deze periode geen punten verdiend.')).toBeInTheDocument();
  });

  it('follows the period control: the weeks and cycles aligned with the other reports', async () => {
    const fetchMock = setup(WORKLOAD, pointsRoutes());
    renderWithProviders(<StatsPage now={NOW} />);
    await selectStatsTab('Punten');
    await screen.findByRole('table', { name: 'Punten per persoon' });

    fireEvent.change(screen.getByLabelText('Periode'), { target: { value: 'weeks:3' } });
    await waitFor(() => expect(pointsUrls(fetchMock)).toContain('/api/points/balances?from=2026-08-31&to=2026-09-20'));
    fireEvent.change(screen.getByLabelText('Periode'), { target: { value: 'cycles:2' } });
    await waitFor(() => expect(pointsUrls(fetchMock)).toContain('/api/points/balances?from=2026-09-14&to=2026-10-11'));
    await waitFor(() =>
      expect(pointsUrls(fetchMock)).toContain(
        '/api/points/entries?personId=a00000000000000000000001&from=2026-09-14&to=2026-10-11',
      ),
    );
  });

  it('says so when nobody earned points in the period', async () => {
    setup(
      WORKLOAD,
      pointsRoutes({ from: '2026-09-14', to: '2026-09-20', balances: [{ personId: ANNA._id, points: 0, executions: 0 }] }),
    );
    renderWithProviders(<StatsPage now={NOW} />);
    await selectStatsTab('Punten');
    expect(await screen.findByText('In deze periode zijn nog geen punten verdiend.')).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Punten per persoon' })).not.toBeInTheDocument();
  });
});
