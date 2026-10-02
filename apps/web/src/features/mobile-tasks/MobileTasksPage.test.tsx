import type { OccurrenceView } from '@huishoudplanner/shared';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import {
  makeOccurrence,
  makeRoom,
  makeSettings,
  makeTask,
  renderWithProviders,
} from '../../test/render.tsx';
import { MobileTasksPage } from './MobileTasksPage.tsx';
import { taskOverviewRows } from './taskOverviewModel.ts';

const NOW = new Date('2026-09-16T08:00:00Z');
const LIVING = makeRoom({ _id: 'r1', name: 'Woonkamer', sortOrder: 1 });
const BEDROOM = makeRoom({ _id: 'r2', name: 'Slaapkamer', sortOrder: 2 });
const VACUUM = makeTask({ _id: 't1', name: 'Stofzuigen', roomId: LIVING._id });
const BEDDING = makeTask({ _id: 't2', name: 'Beddengoed', roomId: BEDROOM._id });

const planned: OccurrenceView[] = [
  makeOccurrence({ _id: 'o1', taskId: VACUUM._id, taskNameSnapshot: VACUUM.name, date: '2026-09-16', assigneeId: ANNA._id }),
  makeOccurrence({ _id: 'o2', taskId: VACUUM._id, taskNameSnapshot: VACUUM.name, date: '2026-09-23', assigneeId: ANNA._id }),
  makeOccurrence({ _id: 'o3', taskId: VACUUM._id, taskNameSnapshot: VACUUM.name, date: '2026-09-30', assigneeId: ANNA._id }),
  makeOccurrence({ _id: 'o4', taskId: VACUUM._id, taskNameSnapshot: VACUUM.name, date: '2026-10-07', assigneeId: ANNA._id }),
  makeOccurrence({ _id: 'o5', taskId: BEDDING._id, taskNameSnapshot: BEDDING.name, date: '2026-09-18' }),
];
const assignedToSomeoneElse = makeOccurrence({
  _id: 'o-other',
  taskId: BEDDING._id,
  taskNameSnapshot: BEDDING.name,
  date: '2026-09-19',
  assigneeId: BRAM._id,
});

function setup() {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': makeSettings(),
    '/api/rooms': [LIVING, BEDROOM],
    '/api/tasks': [VACUUM, BEDDING],
    '/api/occurrences': (_init: RequestInit | undefined, url: string) => {
      const query = new URL(url, 'http://localhost').searchParams;
      const from = query.get('from') ?? '';
      const to = query.get('to') ?? '';
      return [...planned, assignedToSomeoneElse].filter(
        (occurrence) => occurrence.date >= from && occurrence.date <= to,
      );
    },
  });
}

describe('taskOverviewRows', () => {
  it('groups repeated occurrences into one task row with unique sorted dates', () => {
    const duplicate = makeOccurrence({
      _id: 'duplicate',
      taskId: VACUUM._id,
      taskNameSnapshot: VACUUM.name,
      date: '2026-09-16',
    });
    const rows = taskOverviewRows([...planned, duplicate], [VACUUM, BEDDING], [LIVING, BEDROOM], 'Onbekend', '2026-09-16', '2026-09-14');
    expect(rows.find((row) => row.taskId === VACUUM._id && row.periodStart === '2026-09-16')).toMatchObject({
      dates: ['2026-09-16'], cycleWeek: 1,
    });
    expect(rows.find((row) => row.taskId === VACUUM._id && row.periodStart === '2026-09-23')).toMatchObject({
      dates: ['2026-09-23'], cycleWeek: 2,
    });
    expect(rows.find((row) => row.taskId === BEDDING._id)).toMatchObject({ dates: ['2026-09-18'], cycleWeek: 1 });
  });

  it('keeps historical room snapshots separate after a task moves rooms', () => {
    const movedTask = { ...VACUUM, roomId: BEDROOM._id };
    const rows = taskOverviewRows(
      [
        makeOccurrence({
          _id: 'old-room',
          taskId: VACUUM._id,
          taskNameSnapshot: VACUUM.name,
          date: '2026-09-16',
          roomIdSnapshot: LIVING._id,
          roomNameSnapshot: LIVING.name,
        }),
        makeOccurrence({
          _id: 'new-room',
          taskId: VACUUM._id,
          taskNameSnapshot: VACUUM.name,
          date: '2026-09-23',
          roomIdSnapshot: BEDROOM._id,
          roomNameSnapshot: BEDROOM.name,
        }),
      ],
      [movedTask],
      [LIVING, BEDROOM],
      'Onbekend',
      '2026-09-16',
      '2026-09-14',
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
    const firstRender = renderWithProviders(<MobileTasksPage now={NOW} />);
    await screen.findByRole('row', { name: /Stofzuigen/ });
    fireEvent.click(screen.getByRole('button', { name: '2 weken' }));
    firstRender.unmount();
    renderWithProviders(<MobileTasksPage now={NOW} />);
    expect(await screen.findByRole('button', { name: '2 weken' })).toHaveAttribute('aria-pressed', 'true');
    const block = await screen.findByRole('region', { name: '23 sep t/m 29 sep' });
    expect(within(block).getByRole('row', { name: /Stofzuigen/ })).toHaveTextContent('Cyclusweek 2');
    fireEvent.click(screen.getByRole('button', { name: 'Filters herstellen' }));
    expect(await screen.findByRole('button', { name: '1 week' })).toHaveAttribute('aria-pressed', 'true');
  });
});
