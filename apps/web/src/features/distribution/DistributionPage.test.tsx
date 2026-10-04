import type { CyclePlan, PlanSlot } from '../planner/api.ts';
import { fireEvent, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { makeUser, mockApi, storeProfile, page, v2Basics, LIMITS } from '../../test/fixtures.ts';
import { makeRoomV2, makeSettings, makeTaskV2, renderWithProviders } from '../../test/render.tsx';
import { DistributionPage } from './DistributionPage.tsx';

const ANNA = makeUser({ id: 'a00000000000000000000001', name: 'Anna' });
const BRAM = makeUser({ id: 'b00000000000000000000002', name: 'Bram' });
const STAMP = '2026-09-14T08:00:00.000Z';
const slot = (
  taskId: string,
  weekIndex: number,
  weekday: number,
  assigneeId: string | null,
): PlanSlot => ({
  taskId,
  weekIndex,
  weekday,
  assigneeId,
  sortOrder: 0,
});

function plan(slots: PlanSlot[]): CyclePlan {
  return {
    id: 'p1',
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
    version: 1,
  };
}

describe('DistributionPage', () => {
  it('keeps the selected plan after remount and offers a reset', async () => {
    storeProfile(ANNA.id);
    mockApi({
      ...v2Basics(),
      '/api/v2/users': page([ANNA]),
      '/api/v2/tasks': page([]),
      '/api/v2/rooms': page([]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/cycle-plans': page([plan([]), { ...plan([]), id: 'p2', name: 'Zomer', active: false }]),
    });
    const first = renderWithProviders(<DistributionPage />);
    const select = await screen.findByLabelText('Plan');
    fireEvent.change(select, { target: { value: 'p2' } });
    expect(select).toHaveValue('p2');
    first.unmount();
    renderWithProviders(<DistributionPage />);
    expect(await screen.findByLabelText('Plan')).toHaveValue('p2');
    fireEvent.click(screen.getByRole('button', { name: 'Plankeuze herstellen' }));
    expect(screen.getByLabelText('Plan')).toHaveValue('p1');
  });

  it('shows weekday and weekend minutes per person for the week and cycle', async () => {
    const weekly = makeTaskV2({
      id: 't1',
      name: 'Wekelijkse taak',
      roomId: 'r1',
      intervalKey: '1w',
      durationMinutes: 40,
    });
    const small = makeTaskV2({
      id: 't2',
      name: 'Kleine taak',
      roomId: 'r1',
      intervalKey: '1w',
      durationMinutes: 30,
    });
    storeProfile(ANNA.id);
    mockApi({
      ...v2Basics(),
      '/api/v2/users': page([ANNA, BRAM]),
      '/api/v2/tasks': page([weekly, small]),
      '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Woonkamer' })]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/cycle-plans': page([
        plan([
          slot('t1', 0, 1, ANNA.id),
          slot('t2', 0, 6, ANNA.id),
          slot('t2', 0, 2, BRAM.id),
          slot('t1', 0, 0, BRAM.id),
          slot('t1', 1, 1, BRAM.id),
        ]),
      ]),
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
    const task = makeTaskV2({
      id: 't1',
      name: 'Te volle weektaak',
      roomId: 'r1',
      intervalKey: '1w',
      durationMinutes: 62,
    });
    storeProfile(ANNA.id);
    mockApi({
      ...v2Basics(),
      '/api/v2/users': page([ANNA]),
      '/api/v2/tasks': page([task]),
      '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Woonkamer' })]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/cycle-plans': page([plan([slot('t1', 1, 1, ANNA.id)])]),
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
    const task = makeTaskV2({
      id: 't1',
      name: 'Koelkast schoonmaken',
      roomId: 'r1',
      intervalKey: '2wk',
      durationMinutes: 25,
    });
    storeProfile(ANNA.id);
    mockApi({
      ...v2Basics(),
      '/api/v2/users': page([ANNA, BRAM]),
      '/api/v2/tasks': page([task]),
      '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Keuken' })]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/cycle-plans': page([plan([slot('t1', 0, 1, ANNA.id), slot('t1', 2, 1, ANNA.id)])]),
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

  it('takes the length of the cycle from the server limits when it judges the spacing', async () => {
    const task = makeTaskV2({ id: 't1', name: 'Koelkast schoonmaken', roomId: 'r1', intervalKey: '2wk', durationMinutes: 25 });
    storeProfile(ANNA.id);
    mockApi({
      ...v2Basics(),
      '/api/v2/meta/limits': { ...LIMITS, calendar: { ...LIMITS.calendar, cycleDays: 14 } },
      '/api/v2/users': page([ANNA]),
      '/api/v2/tasks': page([task]),
      '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Keuken' })]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/cycle-plans': page([plan([slot('t1', 0, 1, ANNA.id), slot('t1', 2, 1, ANNA.id)])]),
    });
    renderWithProviders(<DistributionPage />, { route: '/distribution?tab=spacing' });

    const spacing = await screen.findByRole('region', { name: 'Spreiding over de cyclus' });
    // 14 days per cycle over 2 placements is a wish of 7 days; with 28 days it would read 14.
    expect(spacing).toHaveTextContent('Gewenst: ongeveer elke 7 dagen');
    expect(spacing).toHaveTextContent('Kan gelijkmatiger');
  });

  it('reads the plans, tasks and rooms from /api/v2, hides discarded plans and ignores inactive tasks', async () => {
    storeProfile(ANNA.id);
    const fetchMock = mockApi({
      ...v2Basics(),
      '/api/v2/users': page([ANNA]),
      '/api/v2/tasks': page([
        makeTaskV2({ id: 't1', name: 'Actief', roomId: 'r1', durationMinutes: 20 }),
        makeTaskV2({ id: 't2', name: 'Inactief', roomId: 'r1', durationMinutes: 50, active: false }),
      ]),
      '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Woonkamer' })]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/cycle-plans': page([
        { ...plan([]), id: 'p0', name: 'Weggegooid', discarded: true },
        plan([slot('t1', 0, 1, ANNA.id), slot('t2', 0, 1, ANNA.id)]),
      ]),
    });
    renderWithProviders(<DistributionPage />);

    const select = await screen.findByLabelText('Plan');
    expect(within(select).getAllByRole('option').map((option) => option.textContent?.trim())).toEqual(['Standaard (actief)']);
    const week = within(await screen.findByRole('region', { name: 'Verdeling van het werk' }))
      .getByRole('heading', { name: 'Week 1' }).parentElement!.parentElement!;
    expect(within(week).getByText('Anna').parentElement!.parentElement!.parentElement!).toHaveTextContent('20 min totaal');
    const urls = fetchMock.mock.calls.map(([url]) => String(url));
    expect(urls).toContain('/api/v2/cycle-plans?limit=200');
    expect(urls).toContain('/api/v2/tasks?limit=200');
    expect(urls).toContain('/api/v2/rooms?limit=200');
    expect(urls.some((url) => /^\/api\/(tasks|rooms|cycle-plans)/.test(url))).toBe(false);
  });
});
