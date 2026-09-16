import type { CyclePlan, Slot } from '@huishoudplanner/shared';
import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { makeUser, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeRoom, makeSettings, makeTask, renderWithProviders } from '../../test/render.tsx';
import { DistributionPage } from './DistributionPage.tsx';

const ANNA = makeUser({ _id: 'a00000000000000000000001', name: 'Anna' });
const BRAM = makeUser({ _id: 'b00000000000000000000002', name: 'Bram' });
const STAMP = '2026-09-14T08:00:00.000Z';
const slot = (
  taskId: string,
  weekIndex: number,
  weekday: number,
  assigneeId: string | null,
): Slot => ({
  taskId,
  weekIndex,
  weekday,
  assigneeId,
  sortOrder: 0,
});

function plan(slots: Slot[]): CyclePlan {
  return {
    _id: 'p1',
    name: 'Standaard',
    active: true,
    slots,
    weekThemes: ['', '', '', ''],
    draft: false,
    source: 'manual',
    proposalId: null,
    rationale: null,
    discarded: false,
    createdAt: STAMP,
    updatedAt: STAMP,
  };
}

describe('DistributionPage', () => {
  it('shows weekday and weekend minutes per person for the week and cycle', async () => {
    const weekly = makeTask({
      _id: 't1',
      name: 'Wekelijkse taak',
      roomId: 'r1',
      intervalKey: '1w',
      durationMinutes: 40,
    });
    const small = makeTask({
      _id: 't2',
      name: 'Kleine taak',
      roomId: 'r1',
      intervalKey: '1w',
      durationMinutes: 30,
    });
    storeProfile(ANNA._id);
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/tasks': [weekly, small],
      '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
      '/api/settings': makeSettings(),
      '/api/cycle-plans': [
        plan([
          slot('t1', 0, 1, ANNA._id),
          slot('t2', 0, 6, ANNA._id),
          slot('t2', 0, 2, BRAM._id),
          slot('t1', 0, 0, BRAM._id),
          slot('t1', 1, 1, BRAM._id),
        ]),
      ],
    });
    renderWithProviders(<DistributionPage />);

    expect(await screen.findByRole('tab', { name: 'Verdeling van het werk' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    const distribution = await screen.findByRole('region', { name: 'Verdeling van het werk' });
    expect(within(distribution).getAllByRole('heading').map((heading) => heading.textContent)).toEqual([
      'Verdeling van het werk',
      'Week 1',
      'Week 2',
      'Week 3',
      'Week 4',
      'Hele cyclus (4 weken)',
    ]);
    const week = within(distribution).getByRole('heading', { name: 'Week 1' }).parentElement!
      .parentElement!;
    const anna = within(week).getByText('Anna').parentElement!.parentElement!.parentElement!;
    expect(anna).toHaveTextContent('70 min totaal');
    expect(anna).toHaveTextContent('Doordeweeks40 / 60 min (67%)');
    expect(anna).toHaveTextContent('Weekend30 / 120 min (25%)');
    expect(
      within(anna).getByRole('meter', { name: 'Belasting doordeweeks van Anna' }),
    ).toHaveAttribute('aria-valuenow', '67');

    const bram = within(week).getByText('Bram').parentElement!.parentElement!.parentElement!;
    expect(bram).toHaveTextContent('Doordeweeks30 / 60 min (50%)');
    expect(bram).toHaveTextContent('Weekend40 / 120 min (33%)');

    const weekTwo = within(distribution).getByRole('heading', { name: 'Week 2' }).parentElement!
      .parentElement!;
    const bramWeekTwo =
      within(weekTwo).getByText('Bram').parentElement!.parentElement!.parentElement!;
    expect(bramWeekTwo).toHaveTextContent('Doordeweeks40 / 60 min (67%)');
    expect(bramWeekTwo).toHaveTextContent('Weekend0 / 120 min (0%)');
    const cycle = within(distribution).getByRole('heading', { name: 'Hele cyclus (4 weken)' })
      .parentElement!.parentElement!;
    const bramCycle = within(cycle).getByText('Bram').parentElement!.parentElement!.parentElement!;
    expect(bramCycle).toHaveTextContent('110 min totaal');
    expect(bramCycle).toHaveTextContent('Doordeweeks70 / 240 min (29%)');
    expect(bramCycle).toHaveTextContent('Weekend40 / 480 min (8%)');
  });

  it('shows and marks a workload above the configured maximum', async () => {
    const task = makeTask({
      _id: 't1',
      name: 'Te volle weektaak',
      roomId: 'r1',
      intervalKey: '1w',
      durationMinutes: 62,
    });
    storeProfile(ANNA._id);
    mockApi({
      '/api/users': [ANNA],
      '/api/tasks': [task],
      '/api/rooms': [makeRoom({ _id: 'r1', name: 'Woonkamer' })],
      '/api/settings': makeSettings(),
      '/api/cycle-plans': [plan([slot('t1', 1, 1, ANNA._id)])],
    });
    renderWithProviders(<DistributionPage />);

    const distribution = await screen.findByRole('region', { name: 'Verdeling van het werk' });
    const weekTwo = within(distribution).getByRole('heading', { name: 'Week 2' }).parentElement!
      .parentElement!;
    const meter = within(weekTwo).getByRole('meter', {
      name: 'Belasting doordeweeks van Anna',
    });
    const person = within(weekTwo).getByText('Anna').parentElement!.parentElement!.parentElement!;
    expect(person).toHaveTextContent('Doordeweeks62 / 60 min (103%)');
    expect(meter).toHaveAttribute('aria-valuenow', '100');
    expect(meter).toHaveAttribute('aria-valuetext', '62 van 60 min (103%)');
  });

  it('keeps the recurring-task spacing assessment on the separate page', async () => {
    const task = makeTask({
      _id: 't1',
      name: 'Koelkast schoonmaken',
      roomId: 'r1',
      intervalKey: '2wk',
      durationMinutes: 25,
    });
    storeProfile(ANNA._id);
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/tasks': [task],
      '/api/rooms': [makeRoom({ _id: 'r1', name: 'Keuken' })],
      '/api/settings': makeSettings(),
      '/api/cycle-plans': [plan([slot('t1', 0, 1, ANNA._id), slot('t1', 2, 1, ANNA._id)])],
    });
    renderWithProviders(<DistributionPage />, { route: '/distribution?tab=spacing' });

    const spacingTab = await screen.findByRole('tab', { name: 'Spreiding over de cyclus' });
    expect(spacingTab).toHaveAttribute('aria-selected', 'true');
    const spacing = await screen.findByRole('region', { name: 'Spreiding over de cyclus' });
    expect(screen.queryByRole('button', { name: 'Week 1' })).not.toBeInTheDocument();
    expect(spacing).toHaveTextContent('Koelkast schoonmaken');
    expect(spacing).toHaveTextContent('Keuken · 1x per 2 weken');
    expect(spacing).toHaveTextContent('werkelijk: 14 dagen');
    expect(spacing).toHaveTextContent('Goed verdeeld');
  });
});
