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
    expect(taskOverviewRows([...planned, duplicate], [VACUUM, BEDDING], [LIVING, BEDROOM], 'Onbekend')).toEqual([
      {
        taskId: BEDDING._id,
        taskName: BEDDING.name,
        roomId: BEDROOM._id,
        roomName: BEDROOM.name,
        dates: ['2026-09-18'],
      },
      {
        taskId: VACUUM._id,
        taskName: VACUUM.name,
        roomId: LIVING._id,
        roomName: LIVING.name,
        dates: ['2026-09-16', '2026-09-23', '2026-09-30', '2026-10-07'],
      },
    ]);
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
    );

    expect(rows.map((row) => [row.roomName, row.dates])).toEqual([
      ['Slaapkamer', ['2026-09-23']],
      ['Woonkamer', ['2026-09-16']],
    ]);
  });
});

describe('MobileTasksPage', () => {
  beforeEach(() => {
    setup();
  });

  it('shows one calm table row per task with comma-separated dates for two weeks by default', async () => {
    renderWithProviders(<MobileTasksPage now={NOW} />);

    expect(await screen.findByRole('heading', { name: 'Mijn taken' })).toBeInTheDocument();
    expect(screen.getByText('16 sep t/m 29 sep')).toBeInTheDocument();
    const mine = screen.getByRole('region', { name: 'Aan mij toegewezen' });
    const unassigned = screen.getByRole('region', { name: 'Nog niet toegewezen' });
    const vacuumRow = within(mine).getByRole('row', { name: /Stofzuigen/ });
    expect(within(vacuumRow).getByText('Woonkamer')).toBeInTheDocument();
    expect(within(vacuumRow).getByText('wo 16 sep, wo 23 sep')).toBeInTheDocument();
    expect(within(unassigned).getByRole('row', { name: /Beddengoed/ })).toBeInTheDocument();
    expect(screen.queryByText('za 19 sep')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: '2 weken' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('checkbox', { name: 'Woonkamer' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Slaapkamer' })).toBeChecked();
  });

  it('can limit the date range and filter rows by room', async () => {
    renderWithProviders(<MobileTasksPage now={NOW} />);
    await screen.findByRole('row', { name: /Stofzuigen/ });

    fireEvent.click(screen.getByRole('button', { name: '1 week' }));
    await waitFor(() => expect(screen.getByText('16 sep t/m 22 sep')).toBeInTheDocument());
    expect(within(screen.getByRole('row', { name: /Stofzuigen/ })).getByText('wo 16 sep')).toBeInTheDocument();
    expect(screen.queryByText('wo 23 sep')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('checkbox', { name: 'Woonkamer' }));
    expect(screen.getByRole('row', { name: /Beddengoed/ })).toBeInTheDocument();
    expect(screen.queryByRole('row', { name: /Stofzuigen/ })).not.toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Slaapkamer' })).toBeChecked();
  });
});
