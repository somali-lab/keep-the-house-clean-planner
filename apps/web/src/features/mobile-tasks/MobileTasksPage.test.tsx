import type { Occurrence } from '../../api/index.ts';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, page, storeProfile, v2Basics } from '../../test/fixtures.ts';
import {
  makeOccurrenceV2,
  makeRoomV2,
  makeSettings,
  makeTaskV2,
  renderWithProviders,
} from '../../test/render.tsx';
import { MobileTasksPage } from './MobileTasksPage.tsx';
import { taskOverviewRows } from './taskOverviewModel.ts';

const NOW = new Date('2026-09-16T08:00:00Z');
const LIVING = makeRoomV2({ id: 'r1', name: 'Woonkamer', sortOrder: 1 });
const BEDROOM = makeRoomV2({ id: 'r2', name: 'Slaapkamer', sortOrder: 2 });
const VACUUM = makeTaskV2({ id: 't1', name: 'Stofzuigen', roomId: LIVING.id });
const BEDDING = makeTaskV2({ id: 't2', name: 'Beddengoed', roomId: BEDROOM.id });

const planned: Occurrence[] = [
  makeOccurrenceV2({ id: 'o1', taskId: VACUUM.id, taskNameSnapshot: VACUUM.name, date: '2026-09-16', assigneeId: ANNA.id }),
  makeOccurrenceV2({ id: 'o2', taskId: VACUUM.id, taskNameSnapshot: VACUUM.name, date: '2026-09-23', assigneeId: ANNA.id }),
  makeOccurrenceV2({ id: 'o3', taskId: VACUUM.id, taskNameSnapshot: VACUUM.name, date: '2026-09-30', assigneeId: ANNA.id }),
  makeOccurrenceV2({ id: 'o4', taskId: VACUUM.id, taskNameSnapshot: VACUUM.name, date: '2026-10-07', assigneeId: ANNA.id }),
  makeOccurrenceV2({ id: 'o5', taskId: BEDDING.id, taskNameSnapshot: BEDDING.name, date: '2026-09-18' }),
];
const assignedToSomeoneElse = makeOccurrenceV2({
  id: 'o-other',
  taskId: BEDDING.id,
  taskNameSnapshot: BEDDING.name,
  date: '2026-09-19',
  assigneeId: BRAM.id,
});

function setup(routes: Record<string, unknown> = {}) {
  storeProfile(ANNA.id);
  return mockApi({
    '/api/v2/users': page([ANNA, BRAM]),
    '/api/v2/settings': makeSettings(),
    '/api/v2/rooms': page([LIVING, BEDROOM]),
    '/api/v2/tasks': page([VACUUM, BEDDING]),
    ...v2Basics(),
    '/api/v2/occurrences': (_init: RequestInit | undefined, url: string) => {
      const query = new URL(url, 'http://localhost').searchParams;
      const from = query.get('from') ?? '';
      const to = query.get('to') ?? '';
      return page(
        [...planned, assignedToSomeoneElse].filter((occurrence) => occurrence.date >= from && occurrence.date <= to),
      );
    },
    ...routes,
  });
}

describe('taskOverviewRows', () => {
  it('groups repeated occurrences into one task row with unique sorted dates', () => {
    const duplicate = makeOccurrenceV2({
      id: 'duplicate',
      taskId: VACUUM.id,
      taskNameSnapshot: VACUUM.name,
      date: '2026-09-16',
    });
    const rows = taskOverviewRows([...planned, duplicate], [VACUUM, BEDDING], [LIVING, BEDROOM], 'Onbekend', '2026-09-16');
    expect(rows.find((row) => row.taskId === VACUUM.id && row.periodStart === '2026-09-16')).toMatchObject({
      dates: ['2026-09-16'], cycleWeek: 1,
    });
    expect(rows.find((row) => row.taskId === VACUUM.id && row.periodStart === '2026-09-23')).toMatchObject({
      dates: ['2026-09-23'], cycleWeek: 2,
    });
    expect(rows.find((row) => row.taskId === BEDDING.id)).toMatchObject({ dates: ['2026-09-18'], cycleWeek: 1 });
  });

  it('keeps historical room snapshots separate after a task moves rooms', () => {
    const movedTask = { ...VACUUM, roomId: BEDROOM.id };
    const rows = taskOverviewRows(
      [
        makeOccurrenceV2({
          id: 'old-room',
          taskId: VACUUM.id,
          taskNameSnapshot: VACUUM.name,
          date: '2026-09-16',
          roomIdSnapshot: LIVING.id,
          roomNameSnapshot: LIVING.name,
        }),
        makeOccurrenceV2({
          id: 'new-room',
          taskId: VACUUM.id,
          taskNameSnapshot: VACUUM.name,
          date: '2026-09-23',
          roomIdSnapshot: BEDROOM.id,
          roomNameSnapshot: BEDROOM.name,
        }),
      ],
      [movedTask],
      [LIVING, BEDROOM],
      'Onbekend',
      '2026-09-16',
    );

    expect(rows.map((row) => [row.roomName, row.dates])).toEqual([
      ['Woonkamer', ['2026-09-16']],
      ['Slaapkamer', ['2026-09-23']],
    ]);
  });
});

describe('MobileTasksPage', () => {
  beforeEach(() => {
    for (const key of Object.keys(localStorage)) if (key.startsWith('huishoudplanner.filters.')) localStorage.removeItem(key);
    setup();
  });

  it('shows one cycle week by default with one calm table row per task', async () => {
    renderWithProviders(<MobileTasksPage now={NOW} />);

    expect(await screen.findByRole('heading', { name: 'Mijn taken' })).toBeInTheDocument();
    expect(screen.getByText('16 sep t/m 22 sep · Cyclusweek 1')).toBeInTheDocument();
    const mine = screen.getByRole('region', { name: 'Aan mij toegewezen' });
    const unassigned = screen.getByRole('region', { name: 'Nog niet toegewezen' });
    const vacuumRow = within(mine).getByRole('row', { name: /Stofzuigen/ });
    expect(within(vacuumRow).getByText('Woonkamer')).toBeInTheDocument();
    expect(within(vacuumRow).getByText('wo 16 sep')).toBeInTheDocument();
    expect(within(unassigned).getByRole('row', { name: /Beddengoed/ })).toBeInTheDocument();
    expect(screen.queryByText('za 19 sep')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: '1 week' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('checkbox', { name: 'Woonkamer' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Slaapkamer' })).toBeChecked();
  });

  it('can extend the date range and filter rows by room', async () => {
    const firstRender = renderWithProviders(<MobileTasksPage now={NOW} />);
    await screen.findByRole('row', { name: /Stofzuigen/ });

    fireEvent.click(screen.getByRole('button', { name: '2 weken' }));
    await waitFor(() => expect(screen.getByText('16 sep t/m 29 sep')).toBeInTheDocument());
    const vacuumRows = screen.getAllByRole('row', { name: /Stofzuigen/ });
    expect(vacuumRows).toHaveLength(2);
    expect(within(vacuumRows[0]!).getByText('wo 16 sep')).toBeInTheDocument();
    expect(within(vacuumRows[1]!).getByText('wo 23 sep')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('checkbox', { name: 'Woonkamer' }));
    expect(screen.getByRole('row', { name: /Beddengoed/ })).toBeInTheDocument();
    expect(screen.queryAllByRole('row', { name: /Stofzuigen/ })).toHaveLength(0);
    expect(screen.getByRole('checkbox', { name: 'Slaapkamer' })).toBeChecked();
    firstRender.unmount();
    renderWithProviders(<MobileTasksPage now={NOW} />);
    expect(await screen.findByRole('checkbox', { name: 'Woonkamer' })).not.toBeChecked();
    expect(screen.getByRole('button', { name: '2 weken' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('splits seven-day blocks at cycle-week boundaries and labels each task cycle week', async () => {
    const firstRender = renderWithProviders(<MobileTasksPage now={NOW} />, { headerReset: true });
    await screen.findByRole('row', { name: /Stofzuigen/ });
    expect(screen.getByRole('button', { name: 'Filters van dit scherm resetten' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: '2 weken' }));
    firstRender.unmount();
    renderWithProviders(<MobileTasksPage now={NOW} />, { headerReset: true });
    expect(await screen.findByRole('button', { name: '2 weken' })).toHaveAttribute('aria-pressed', 'true');
    const block = await screen.findByRole('region', { name: '23 sep t/m 29 sep' });
    expect(within(block).getByRole('row', { name: /Stofzuigen/ })).toHaveTextContent('Cyclusweek 2');
    fireEvent.click(screen.getByRole('button', { name: 'Filters van dit scherm resetten' }));
    expect(await screen.findByRole('button', { name: '1 week' })).toHaveAttribute('aria-pressed', 'true');
  });
  it('opens the Extra Task dialog from the header, on already done, and shows a planned task in its dated block', async () => {
    const created = makeOccurrenceV2({ id: 'o-new', taskId: BEDDING.id, taskNameSnapshot: BEDDING.name, date: '2026-09-17', assigneeId: ANNA.id, origin: 'adhoc' });
    const fetchMock = setup({
      'POST /api/v2/occurrences': () => {
        planned.push(created);
        return created;
      },
    });
    try {
      renderWithProviders(<MobileTasksPage now={NOW} />);
      const mine = await screen.findByRole('region', { name: 'Aan mij toegewezen' });
      expect(within(mine).queryByRole('row', { name: /Beddengoed/ })).not.toBeInTheDocument();

      fireEvent.click(screen.getByRole('button', { name: 'Extra taak' }));
      const dialog = await screen.findByRole('dialog', { name: 'Extra taak' });
      expect(within(dialog).getByRole('radio', { name: 'Al gedaan (vandaag)' })).toBeChecked();

      fireEvent.click(within(dialog).getByRole('radio', { name: 'Inplannen' }));
      const task = within(dialog).getByLabelText('Taak', { selector: 'select' });
      await waitFor(() => expect(within(task).getAllByRole('option')).toHaveLength(3));
      fireEvent.change(task, { target: { value: BEDDING.id } });
      fireEvent.change(within(dialog).getByLabelText('Datum'), { target: { value: '2026-09-17' } });
      fireEvent.change(within(dialog).getByLabelText('Voor wie'), { target: { value: ANNA.id } });
      fireEvent.click(within(dialog).getByRole('button', { name: 'Inplannen' }));

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
      const posted = fetchMock.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'POST');
      expect(posted).toHaveLength(1);
      // The list was refetched: the planned task shows up under "assigned to me" on its day.
      const row = await within(await screen.findByRole('region', { name: 'Aan mij toegewezen' })).findByRole('row', { name: /Beddengoed/ });
      expect(within(row).getByText('do 17 sep')).toBeInTheDocument();
    } finally {
      planned.splice(planned.indexOf(created), 1);
    }
  });
});
