import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, onTestFinished } from 'vitest';
import { ANNA, BRAM, makeUser, mockApi, page, problem, storeProfile, v2Basics } from '../../test/fixtures.ts';
import { makeRoomV2, makeSettings, makeTaskV2, renderWithProviders } from '../../test/render.tsx';
import type { CompletionReport, DeviationReport, IntervalReport, PointEntry, PointsBalances, WorkloadReport } from './api.ts';
import { StatsPage } from './StatsPage.tsx';

/** An answer with a status of its own: the route table of mockApi answers 200, which for a redemption means a replay. */
const answer = (body: unknown, status: number) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

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
    { userId: ANNA.id, plannedMinutes: anna[0], doneMinutes: anna[1] },
    { userId: BRAM.id, plannedMinutes: bram[0], doneMinutes: bram[1] },
  ],
  unassignedPlannedMinutes: unassigned,
});

const WORKLOAD: WorkloadReport = {
  cycles: [
    {
      index: 0,
      startDate: '2026-09-14',
      endDate: '2026-10-11',
      users: [
        { userId: ANNA.id, plannedMinutes: 165, doneMinutes: 75 },
        { userId: BRAM.id, plannedMinutes: 40, doneMinutes: 70 },
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
        { userId: ANNA.id, plannedMinutes: 120, doneMinutes: 30 },
        { userId: BRAM.id, plannedMinutes: 40, doneMinutes: 0 },
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

const COMPLETION = (groupBy: string): CompletionReport =>
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

const INTERVALS: IntervalReport = {
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

const DEVIATIONS: DeviationReport = {
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

function setup(workload: WorkloadReport = WORKLOAD, extraRoutes: Record<string, unknown> = {}) {
  storeProfile(ANNA.id);
  return mockApi({
    '/api/v2/users': page([ANNA, BRAM]),
    '/api/v2/settings': makeSettings(),
    '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Badkamer' }), makeRoomV2({ id: 'r2', name: 'Keuken' })]),
    '/api/v2/tasks': page([
      makeTaskV2({ id: 't1', name: 'Badkamer schoonmaken', roomId: 'r1' }),
      makeTaskV2({ id: 't2', name: 'Keuken dweilen', roomId: 'r2' }),
      makeTaskV2({ id: 't3', name: 'Ramen lappen', roomId: 'r1' }),
      makeTaskV2({ id: 't4', name: 'Afwas', roomId: 'r2' }),
    ]),
    ...v2Basics(),
    '/api/v2/stats/workload': workload,
    '/api/v2/stats/completion': (_init: RequestInit | undefined, url: string) =>
      COMPLETION(new URL(url, 'http://x').searchParams.get('groupBy') ?? 'task'),
    '/api/v2/stats/intervals': INTERVALS,
    '/api/v2/stats/deviations': DEVIATIONS,
    'DELETE /api/v2/stats': {
      deletedOccurrences: 12,
      deletedRecorded: 3,
      resetOccurrences: 4,
      resetTasks: 2,
      deletedPastCycles: 1,
      removedPointEntries: 5,
      removedRedemptions: 1,
    },
    ...extraRoutes,
  });
}

const statsUrls = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.map(([u]) => String(u)).filter((u) => u.startsWith('/api/v2/stats/'));

/** The reset requests: url and headers (a bulk reset is no entity write, so it carries no If-Match). */
const resets = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls
    .filter(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE')
    .map(([url, init]) => ({ url: String(url), ifMatch: ((init as RequestInit).headers as Record<string, string>)['if-match'] }))
    .filter((call) => call.url.startsWith('/api/v2/stats'));

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
      expect(statsUrls(fetchMock)).toContain('/api/v2/stats/completion?weeks=1&groupBy=room'),
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
          '/api/v2/stats/workload?weeks=3',
          '/api/v2/stats/completion?weeks=3&groupBy=task',
          '/api/v2/stats/intervals?weeks=3',
          '/api/v2/stats/deviations?weeks=3',
        ]),
      ),
    );

    fireEvent.change(period, { target: { value: 'cycles:4' } });
    await waitFor(() =>
      expect(statsUrls(fetchMock)).toEqual(
        expect.arrayContaining([
          '/api/v2/stats/workload?cycles=4',
          '/api/v2/stats/completion?cycles=4&groupBy=task',
          '/api/v2/stats/intervals?cycles=4',
          '/api/v2/stats/deviations?cycles=4',
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
    expect(resets(fetchMock)).toEqual([{ url: '/api/v2/stats', ifMatch: undefined }]);
  });

  it('shows a failed reset inside the confirmation dialog and lets the person try again', async () => {
    let attempts = 0;
    const fetchMock = setup(WORKLOAD, {
      'DELETE /api/v2/stats': () => {
        attempts += 1;
        return attempts === 1
          ? problem(409, 'conflict', 'Busy')
          : { deletedOccurrences: 0, deletedRecorded: 0, resetOccurrences: 0, resetTasks: 0, deletedPastCycles: 0, removedPointEntries: 0, removedRedemptions: 0 };
      },
    });
    renderWithProviders(<StatsPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Statistieken wissen' }));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Definitief opnieuw beginnen' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('De statistieken konden niet worden gewist.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Definitief opnieuw beginnen' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Je begint opnieuw met de bestaande planning.');
    expect(resets(fetchMock)).toHaveLength(2);
  });

  it('offers the reset and the purge to administrators only', async () => {
    setup();
    storeProfile(BRAM.id);
    renderWithProviders(<StatsPage />);
    await screen.findByRole('tab', { name: 'Overzicht' });
    expect(screen.queryByRole('button', { name: 'Statistieken wissen' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Oude data opschonen' })).not.toBeInTheDocument();
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
    expect(resets(fetchMock)).toEqual([{ url: '/api/v2/stats?before=2026-09-21', ifMatch: undefined }]);
  });
});

describe('StatsPage: points', () => {
  const NOW = new Date('2026-09-16T08:00:00.000Z');
  const FORMER = makeUser({ id: 'c00000000000000000000003', name: 'Carla', active: false });
  /** A balance without redemptions or money. */
  const bal = (personId: string, points: number, executions: number, bonusPoints = 0) => ({
    personId,
    points,
    earned: points,
    redeemed: 0,
    money: null,
    executions,
    bonusPoints,
  });
  const BALANCES: PointsBalances = {
    from: '2026-09-14',
    to: '2026-09-20',
    currencyCode: 'EUR',
    centsPerPoint: 0,
    balances: [bal(ANNA.id, 8, 3), bal(BRAM.id, 0, 0), bal(FORMER.id, 4, 1)],
  };
  const entry = (id: string, personId: string, date: string, amount: number, title: string): PointEntry => ({
    id,
    key: `execution:${id}`,
    kind: 'execution',
    personId,
    amount,
    date,
    weekStart: '2026-09-14',
    periodStart: null,
    occurrenceId: id,
    taskId: null,
    titleSnapshot: title,
    note: null,
    centsPerPointSnapshot: null,
    currencyCodeSnapshot: null,
    source: 'live',
    createdAt: '2026-09-16T08:00:00.000Z',
    updatedAt: '2026-09-16T08:00:00.000Z',
  });
  const ENTRIES: Record<string, { entries: PointEntry[] }> = {
    [ANNA.id]: {
      entries: [
        entry('e00000000000000000000001', ANNA.id, '2026-09-16', 5, 'Ramen lappen'),
        entry('e00000000000000000000002', ANNA.id, '2026-09-14', 3, 'Stofzuigen'),
      ],
    },
    [BRAM.id]: { entries: [] },
    [FORMER.id]: { entries: [entry('e00000000000000000000003', FORMER.id, '2026-09-15', 4, 'Afwassen')] },
  };
  const pointsRoutes = (balances: PointsBalances = BALANCES) => ({
    '/api/v2/users': page([ANNA, BRAM, FORMER]),
    '/api/v2/points/balances': balances,
    '/api/v2/points/entries': (_init: RequestInit | undefined, url: string) =>
      page(ENTRIES[new URL(url, 'http://x').searchParams.get('personId') ?? '']?.entries ?? []),
  });
  const pointsUrls = (fetchMock: ReturnType<typeof mockApi>) =>
    fetchMock.mock.calls.map(([u]) => String(u)).filter((u) => u.startsWith('/api/v2/points/'));

  it('shows the balance of every person and the entries of the active profile for the selected period', async () => {
    const fetchMock = setup(WORKLOAD, pointsRoutes());
    renderWithProviders(<StatsPage now={NOW} />);
    await selectStatsTab('Punten');
    const heading = await screen.findByRole('heading', { name: 'Punten' });
    const section = heading.closest('section')!;
    expect(section).toHaveTextContent('14 september 2026 tot 20 september 2026');

    const balances = await within(section).findByRole('table', { name: 'Punten per persoon' });
    // Net in the period, executions, bonus, redeemed, and the all-time balance (the same data here).
    await waitFor(() =>
      expect(within(balances).getAllByRole('row').slice(1).map((row) => row.textContent)).toEqual([
        'Anna83008',
        'Bram de Vries00000',
        'Carla (inactief)41004',
      ]),
    );
    const entries = await within(section).findByRole('table', { name: 'Posten van Anna' });
    expect(within(entries).getAllByRole('row').slice(1).map((row) => row.textContent)).toEqual([
      '16 september 2026Ramen lappen+5',
      '14 september 2026Stofzuigen+3',
    ]);
    // The balance over the whole ledger is read without a range.
    expect([...pointsUrls(fetchMock)].sort()).toEqual([
      '/api/v2/points/balances',
      '/api/v2/points/balances?from=2026-09-14&to=2026-09-20',
      '/api/v2/points/entries?personId=a00000000000000000000001&from=2026-09-14&to=2026-09-20&limit=500',
    ]);
    // The week comes from the calendar of the server (today only), not from arithmetic in the page.
    expect(fetchMock.mock.calls.map(([u]) => String(u))).toContain('/api/v2/calendar?from=2026-09-16&to=2026-09-16');
  });

  it('shows the entries of another person, also of someone who is inactive', async () => {
    setup(WORKLOAD, pointsRoutes());
    renderWithProviders(<StatsPage now={NOW} />);
    await selectStatsTab('Punten');
    const picker = await screen.findByLabelText('Toon posten van');
    fireEvent.change(picker, { target: { value: FORMER.id } });
    const table = await screen.findByRole('table', { name: 'Posten van Carla' });
    expect(within(table).getAllByRole('row').slice(1).map((row) => row.textContent)).toEqual(['15 september 2026Afwassen+4']);

    fireEvent.change(picker, { target: { value: BRAM.id } });
    expect(await screen.findByText('Bram de Vries heeft in deze periode geen punten verdiend.')).toBeInTheDocument();
  });

  it('follows the period control: the weeks and cycles aligned with the other reports', async () => {
    const fetchMock = setup(WORKLOAD, pointsRoutes());
    renderWithProviders(<StatsPage now={NOW} />);
    await selectStatsTab('Punten');
    await screen.findByRole('table', { name: 'Punten per persoon' });

    fireEvent.change(screen.getByLabelText('Periode'), { target: { value: 'weeks:3' } });
    await waitFor(() => expect(pointsUrls(fetchMock)).toContain('/api/v2/points/balances?from=2026-08-31&to=2026-09-20'));
    fireEvent.change(screen.getByLabelText('Periode'), { target: { value: 'cycles:2' } });
    await waitFor(() => expect(pointsUrls(fetchMock)).toContain('/api/v2/points/balances?from=2026-09-14&to=2026-10-11'));
    await waitFor(() =>
      expect(pointsUrls(fetchMock)).toContain(
        '/api/v2/points/entries?personId=a00000000000000000000001&from=2026-09-14&to=2026-10-11&limit=500',
      ),
    );
  });

  it('shows the bonus column and labels bonus entries by kind and period, with an icon', async () => {
    const bonus = (id: string, kind: PointEntry['kind'], date: string, periodStart: string, amount: number): PointEntry => ({
      ...entry(id, ANNA.id, date, amount, ''),
      key: `${kind}:${ANNA.id}:${periodStart}`,
      kind,
      periodStart,
      occurrenceId: null,
    });
    const original = ENTRIES[ANNA.id]!;
    onTestFinished(() => {
      ENTRIES[ANNA.id] = original;
    });
    ENTRIES[ANNA.id] = {
      entries: [
        bonus('f00000000000000000000001', 'bonus_week_ontime', '2026-09-20', '2026-09-14', 3),
        bonus('f00000000000000000000002', 'bonus_week_done', '2026-09-20', '2026-09-14', 5),
        bonus('f00000000000000000000003', 'bonus_cycle_done', '2026-10-04', '2026-09-07', 20),
        bonus('f00000000000000000000004', 'bonus_cycle_ontime', '2026-10-04', '2026-09-07', 10),
        entry('e00000000000000000000002', ANNA.id, '2026-09-14', 3, 'Stofzuigen'),
      ],
    };
    setup(
      WORKLOAD,
      pointsRoutes({
        from: '2026-09-14',
        to: '2026-09-20',
        currencyCode: 'EUR',
        centsPerPoint: 0,
        balances: [bal(ANNA.id, 41, 1, 38)],
      }),
    );
    renderWithProviders(<StatsPage now={NOW} />);
    await selectStatsTab('Punten');
    const balances = await screen.findByRole('table', { name: 'Punten per persoon' });
    expect(within(balances).getAllByRole('columnheader').map((cell) => cell.textContent)).toEqual(['Persoon', 'Netto in periode', 'Uitvoeringen', 'Bonus', 'Ingewisseld', 'Saldo']);
    await waitFor(() => expect(within(balances).getAllByRole('row')[1]!.textContent).toBe('Anna41138041'));

    const entries = await screen.findByRole('table', { name: 'Posten van Anna' });
    const rows = within(entries).getAllByRole('row').slice(1);
    expect(rows.map((row) => row.textContent)).toEqual([
      '20 september 2026Weekbonus: alles op tijd, week 38+3',
      '20 september 2026Weekbonus: alles gedaan, week 38+5',
      '4 oktober 2026Cyclusbonus: alles gedaan, 7 sep – 4 okt+20',
      '4 oktober 2026Cyclusbonus: alles op tijd, 7 sep – 4 okt+10',
      '14 september 2026Stofzuigen+3',
    ]);
    // The label is text next to an icon, so a bonus is not recognisable by colour or icon alone.
    expect(rows[0]!.querySelector('svg')).not.toBeNull();
    expect(rows[4]!.querySelector('svg')).toBeNull();
  });

  describe('redemptions', () => {
    const REDEMPTION_ID = 'd00000000000000000000001';
    const redemption = (id: string, personId: string, date: string, amount: number, note: string | null, cents = 25, currency = 'EUR'): PointEntry => ({
      ...entry(id, personId, date, amount, ''),
      key: `redemption:${id}`,
      kind: 'redemption',
      occurrenceId: null,
      note,
      centsPerPointSnapshot: cents,
      currencyCodeSnapshot: currency,
    });
    const withEntries = (personId: string, entries: PointEntry[]) => {
      const original = ENTRIES[personId]!;
      onTestFinished(() => {
        ENTRIES[personId] = original;
      });
      ENTRIES[personId] = { entries };
    };
    const MONEY: PointsBalances = {
      from: '2026-09-14',
      to: '2026-09-20',
      currencyCode: 'EUR',
      centsPerPoint: 25,
      balances: [
        { ...bal(ANNA.id, 6, 3), earned: 10, redeemed: 4, money: { earned: 250, redeemed: 100, balance: 150 } },
        { ...bal(BRAM.id, 0, 0), money: { earned: 0, redeemed: 0, balance: 0 } },
      ],
    };

    it('adds the redeemed points to the balances, and the value of the balance when a point is worth money', async () => {
      setup(WORKLOAD, pointsRoutes(MONEY));
      renderWithProviders(<StatsPage now={NOW} />);
      await selectStatsTab('Punten');
      const balances = await screen.findByRole('table', { name: 'Punten per persoon' });
      expect(within(balances).getAllByRole('columnheader').map((cell) => cell.textContent)).toEqual([
        'Persoon',
        'Netto in periode',
        'Uitvoeringen',
        'Bonus',
        'Ingewisseld',
        'Waarde in periode',
        'Saldo',
        'Waarde saldo',
      ]);
      const row = within(balances).getAllByRole('row')[1]!;
      // Period value €1,50 and, from the same data for the all-time balance, 6 points worth €1,50.
      await waitFor(() => expect(row).toHaveTextContent(/^Anna6304€\s1,506€\s1,50$/));
    });

    it('shows the all-time balance next to the net of the period, from the whole ledger', async () => {
      const ALL_TIME: PointsBalances = {
        from: null,
        to: null,
        currencyCode: 'EUR',
        centsPerPoint: 25,
        balances: [
          { ...bal(ANNA.id, 30, 9), earned: 40, redeemed: 10, money: { earned: 1000, redeemed: 250, balance: 750 } },
          { ...bal(BRAM.id, 2, 1), money: { earned: 50, redeemed: 0, balance: 50 } },
        ],
      };
      setup(WORKLOAD, {
        ...pointsRoutes(MONEY),
        '/api/v2/points/balances': (_init: RequestInit | undefined, url: string) => (url.includes('?') ? MONEY : ALL_TIME),
      });
      renderWithProviders(<StatsPage now={NOW} />);
      await selectStatsTab('Punten');
      const balances = await screen.findByRole('table', { name: 'Punten per persoon' });
      const rows = within(balances).getAllByRole('row').slice(1);
      await waitFor(() => expect(rows[0]).toHaveTextContent(/^Anna6304€\s1,5030€\s7,50$/));
      expect(rows[1]).toHaveTextContent(/^Bram de Vries0000€\s0,002€\s0,50$/);
    });

    it('shows no value column while a point is worth nothing', async () => {
      setup(WORKLOAD, pointsRoutes());
      renderWithProviders(<StatsPage now={NOW} />);
      await selectStatsTab('Punten');
      const balances = await screen.findByRole('table', { name: 'Punten per persoon' });
      expect(within(balances).queryByRole('columnheader', { name: /Waarde/ })).not.toBeInTheDocument();
      expect(within(balances).getByRole('columnheader', { name: 'Saldo' })).toBeInTheDocument();
    });

    it('lists a redemption with an icon, its note, the points and what they were worth then, and an undo button for an administrator', async () => {
      withEntries(ANNA.id, [
        redemption(REDEMPTION_ID, ANNA.id, '2026-09-16', -4, 'Pizza', 20),
        redemption('d00000000000000000000002', ANNA.id, '2026-09-15', -1, null, 0),
        entry('e00000000000000000000002', ANNA.id, '2026-09-14', 3, 'Stofzuigen'),
      ]);
      setup(WORKLOAD, pointsRoutes(MONEY));
      renderWithProviders(<StatsPage now={NOW} />);
      await selectStatsTab('Punten');
      const entries = await screen.findByRole('table', { name: 'Posten van Anna' });
      const rows = within(entries).getAllByRole('row').slice(1);
      // The money is the factor at the time of booking (20 cents), not the one in force now (25).
      expect(rows[0]!.textContent).toMatch(/^16 september 2026Ingewisseld: Pizza-4\s\(€\s0,80\)Ongedaan maken$/);
      expect(rows[1]!.textContent).toBe('15 september 2026Ingewisseld-1Ongedaan maken');
      expect(rows[2]!.textContent).toBe('14 september 2026Stofzuigen+3');
      // Icon plus text, not colour alone.
      expect(rows[0]!.querySelector('svg')).not.toBeNull();
      expect(rows[2]!.querySelector('svg')).toBeNull();
      expect(within(entries).getByRole('button', { name: 'Inwisseling van 4 punten ongedaan maken' })).toBeInTheDocument();
    });

    it('shows each redemption in the currency and at the factor it was booked with, also after the household switched', async () => {
      withEntries(ANNA.id, [
        redemption(REDEMPTION_ID, ANNA.id, '2026-09-16', -4, null, 20, 'USD'),
        redemption('d00000000000000000000002', ANNA.id, '2026-09-15', -2, null, 10, 'EUR'),
        { ...redemption('d00000000000000000000003', ANNA.id, '2026-09-14', -1, null, 50), currencyCodeSnapshot: null },
      ]);
      // The household currency now is EUR, 25 cents.
      setup(WORKLOAD, pointsRoutes(MONEY));
      renderWithProviders(<StatsPage now={NOW} />);
      await selectStatsTab('Punten');
      const entries = await screen.findByRole('table', { name: 'Posten van Anna' });
      const rows = within(entries).getAllByRole('row').slice(1).map((row) => row.textContent ?? '');
      expect(rows[0]).toMatch(/-4\s\(US\$\s?0,80\)/);
      expect(rows[1]).toMatch(/-2\s\(€\s0,20\)/);
      // A booking from before the currency was kept falls back to the household currency.
      expect(rows[2]).toMatch(/-1\s\(€\s0,50\)/);
    });

    it('lets a member undo their own redemption of today only, and nobody else\'s', async () => {
      withEntries(BRAM.id, [
        redemption(REDEMPTION_ID, BRAM.id, '2026-09-16', -2, null),
        redemption('d00000000000000000000002', BRAM.id, '2026-09-15', -1, null),
      ]);
      withEntries(ANNA.id, [redemption('d00000000000000000000003', ANNA.id, '2026-09-16', -3, null)]);
      setup(WORKLOAD, pointsRoutes(MONEY));
      storeProfile(BRAM.id);
      renderWithProviders(<StatsPage now={NOW} />);
      await selectStatsTab('Punten');
      const own = await screen.findByRole('table', { name: 'Posten van Bram de Vries' });
      expect(within(own).getAllByRole('button')).toHaveLength(1);
      expect(within(own).getByRole('button', { name: 'Inwisseling van 2 punten ongedaan maken' })).toBeInTheDocument();
      expect(within(own).queryByRole('button', { name: 'Inwisseling van 1 punten ongedaan maken' })).not.toBeInTheDocument();

      fireEvent.change(screen.getByLabelText('Toon posten van'), { target: { value: ANNA.id } });
      const other = await screen.findByRole('table', { name: 'Posten van Anna' });
      expect(within(other).queryByRole('button')).not.toBeInTheDocument();
    });

    it('undoes a redemption and confirms it, and refetches the points', async () => {
      withEntries(ANNA.id, [redemption(REDEMPTION_ID, ANNA.id, '2026-09-16', -4, 'Pizza')]);
      const fetchMock = setup(WORKLOAD, {
        ...pointsRoutes(MONEY),
        [`DELETE /api/v2/points/redemptions/${REDEMPTION_ID}`]: { deleted: true },
      });
      renderWithProviders(<StatsPage now={NOW} />);
      await selectStatsTab('Punten');
      const entries = await screen.findByRole('table', { name: 'Posten van Anna' });
      fireEvent.click(within(entries).getByRole('button', { name: 'Inwisseling van 4 punten ongedaan maken' }));
      expect(await screen.findByRole('status')).toHaveTextContent('De inwisseling is ongedaan gemaakt.');
      const undone = fetchMock.mock.calls.filter(([u, init]) => u === `/api/v2/points/redemptions/${REDEMPTION_ID}` && (init as RequestInit).method === 'DELETE');
      // Undoing is an intent endpoint: no If-Match.
      expect(undone.map(([, init]) => ((init as RequestInit).headers as Record<string, string>)['if-match'])).toEqual([undefined]);
      await waitFor(() => expect(pointsUrls(fetchMock).filter((u) => u.startsWith('/api/v2/points/balances')).length).toBeGreaterThan(2));
    });

    it('says why when the server refuses to undo a redemption of an earlier day', async () => {
      withEntries(ANNA.id, [redemption(REDEMPTION_ID, ANNA.id, '2026-09-16', -4, null)]);
      storeProfile(ANNA.id);
      setup(WORKLOAD, {
        ...pointsRoutes(MONEY),
        [`DELETE /api/v2/points/redemptions/${REDEMPTION_ID}`]: () => problem(403, 'redemption_locked', 'Locked'),
      });
      renderWithProviders(<StatsPage now={NOW} />);
      await selectStatsTab('Punten');
      const entries = await screen.findByRole('table', { name: 'Posten van Anna' });
      fireEvent.click(within(entries).getByRole('button', { name: 'Inwisseling van 4 punten ongedaan maken' }));
      expect(await screen.findByRole('alert')).toHaveTextContent('Een inwisseling kan alleen op de dag zelf ongedaan worden gemaakt');
    });

    it('opens the redeem dialog from the Redeem button and confirms the booking with the money it is worth', async () => {
      // 201: a new booking.
      setup(WORKLOAD, {
        ...pointsRoutes(MONEY),
        'POST /api/v2/points/redemptions': () => answer(redemption(REDEMPTION_ID, ANNA.id, '2026-09-16', -4, null), 201),
      });
      renderWithProviders(<StatsPage now={NOW} />);
      await selectStatsTab('Punten');
      await screen.findByRole('table', { name: 'Punten per persoon' });
      fireEvent.click(screen.getByRole('button', { name: 'Inwisselen' }));
      const dialog = await screen.findByRole('dialog', { name: 'Punten inwisselen' });
      await within(dialog).findByText(/Beschikbaar saldo/);
      fireEvent.change(within(dialog).getByLabelText('Aantal punten'), { target: { value: '4' } });
      fireEvent.click(within(dialog).getByRole('button', { name: 'Inwisselen' }));
      expect(await screen.findByText(/4 punten ingewisseld \(€\s1,00\)\./)).toBeInTheDocument();
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    });

    it('tells the person when the server replayed a redemption it already had, instead of saying it was booked', async () => {
      storeProfile(ANNA.id);
      // 200: the server already had this request and replayed it.
      setup(WORKLOAD, {
        ...pointsRoutes(MONEY),
        'POST /api/v2/points/redemptions': () => answer(redemption(REDEMPTION_ID, ANNA.id, '2026-09-16', -4, null), 200),
      });
      renderWithProviders(<StatsPage now={NOW} />);
      await selectStatsTab('Punten');
      await screen.findByRole('table', { name: 'Punten per persoon' });
      fireEvent.click(screen.getByRole('button', { name: 'Inwisselen' }));
      const dialog = await screen.findByRole('dialog', { name: 'Punten inwisselen' });
      await within(dialog).findByText(/Beschikbaar saldo/);
      fireEvent.change(within(dialog).getByLabelText('Aantal punten'), { target: { value: '4' } });
      fireEvent.click(within(dialog).getByRole('button', { name: 'Inwisselen' }));
      expect(await screen.findByText('Deze inwisseling was al geboekt.')).toBeInTheDocument();
      expect(screen.queryByText(/4 punten ingewisseld/)).not.toBeInTheDocument();
    });
  });

  it('says so when nobody earned points in the period', async () => {
    setup(
      WORKLOAD,
      pointsRoutes({ from: '2026-09-14', to: '2026-09-20', currencyCode: 'EUR', centsPerPoint: 0, balances: [bal(ANNA.id, 0, 0)] }),
    );
    renderWithProviders(<StatsPage now={NOW} />);
    await selectStatsTab('Punten');
    expect(await screen.findByText('In deze periode zijn nog geen punten verdiend.')).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Punten per persoon' })).not.toBeInTheDocument();
  });
});
