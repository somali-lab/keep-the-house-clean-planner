import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router';
import { FilterResetButton, FilterResetProvider } from '@/components/FilterReset';
import type { Occurrence } from '../api/index.ts';
import type { Settings } from '../api/v2/household.ts';
import type { Room as RoomV2, Task as TaskV2 } from '../api/v2/queries.ts';
import { ProfileProvider } from '../identity/index.ts';
import { testQueryClient } from './fixtures.ts';

const STAMP = '2026-09-14T08:00:00.000Z';

/** A room as `GET /api/v2/rooms` returns it (field `id`). */
export function makeRoomV2(overrides: Partial<RoomV2> & Pick<RoomV2, 'id' | 'name'>): RoomV2 {
  return { sortOrder: 10, active: true, virtual: false, createdAt: STAMP, updatedAt: STAMP, version: 1, ...overrides };
}

/** A task as `GET /api/v2/tasks` returns it (field `id`, points always set). */
export function makeTaskV2(overrides: Partial<TaskV2> & Pick<TaskV2, 'id' | 'name' | 'roomId'>): TaskV2 {
  return {
    intervalKey: '1w',
    durationMinutes: 15,
    points: 15,
    defaultAssigneeId: null,
    active: true,
    notes: '',
    tags: [],
    lastCompletedAt: null,
    createdAt: STAMP,
    updatedAt: STAMP,
    version: 1,
    ...overrides,
  };
}

/** The week of the four-week cycle that starts on Monday 2026-09-14, the cycle of the tests; 0 before it. */
function testWeekIndex(date: string): number {
  const days = Math.round((Date.parse(`${date}T00:00:00Z`) - Date.parse('2026-09-14T00:00:00Z')) / 86_400_000);
  return days < 0 ? 0 : Math.floor(days / 7) % 4;
}

/** An occurrence as `GET /api/v2/occurrences` returns it (field `id`, with the cycle position the server computes). */
export function makeOccurrenceV2(overrides: Partial<Occurrence> & Pick<Occurrence, 'id'>): Occurrence {
  const date = overrides.date ?? '2026-09-16';
  return {
    taskId: 't1',
    cycleId: 'c00000000000000000000001',
    planId: null,
    date,
    plannedDate: overrides.plannedDate ?? date,
    assigneeId: null,
    status: 'open',
    statusBeforeCompletion: null,
    completedAt: null,
    completedBy: null,
    skipReason: null,
    durationMinutesSnapshot: 15,
    taskNameSnapshot: 'Taak',
    roomIdSnapshot: null,
    roomNameSnapshot: null,
    origin: 'generated',
    recordedDone: false,
    pointsSnapshot: null,
    pointsOverride: null,
    createdAt: STAMP,
    updatedAt: STAMP,
    isOverdue: false,
    movedFrom: null,
    cycleIndex: date >= '2026-09-14' ? 0 : -1,
    weekIndex: testWeekIndex(date),
    ...overrides,
  };
}

/** The intervals of a new household, as `GET /api/v2/settings` answers them. */
export const DEFAULT_INTERVALS = [
  { key: 'daily', label: 'Dagelijks', perCycle: 28, periodDays: 1 },
  { key: '3w', label: '3x per week', perCycle: 12, periodDays: 2 },
  { key: '2w', label: '2x per week', perCycle: 8, periodDays: 3 },
  { key: '1w', label: '1x per week', perCycle: 4, periodDays: 7 },
  { key: '2wk', label: '1x per 2 weken', perCycle: 2, periodDays: 14 },
  { key: '4wk', label: '1x per 4 weken', perCycle: 1, periodDays: 28 },
  { key: 'quarter', label: '1x per kwartaal', perCycle: null, periodDays: 91 },
];

/** The household settings as `GET /api/v2/settings` answers them (defaults delivered, bonuses in force worked out by the server). */
export function makeSettings(overrides: Partial<Settings> = {}): Settings {
  return {
    id: 's00000000000000000000001',
    cycleAnchorDate: '2026-09-14',
    weekStartsOn: 1,
    timezone: 'Europe/Amsterdam',
    vacationRanges: [],
    intervals: DEFAULT_INTERVALS,
    aiProvider: { type: 'none', endpoint: null, model: null, timeoutSeconds: null },
    aiPromptTemplates: null,
    completionControl: 'circle',
    promoteThreshold: 2,
    bonusSchedule: [],
    bonusesInForce: { weekDone: 0, weekOnTime: 0, cycleDone: 0, cycleOnTime: 0 },
    currencyCode: 'EUR',
    centsPerPoint: 0,
    rewardGoals: { weekPoints: null, cyclePoints: null },
    createdAt: STAMP,
    updatedAt: STAMP,
    version: 1,
    ...overrides,
  };
}

/**
 * Renders a page with query client, profile context and an in-memory router. Pages register their
 * filter reset with the shared provider; pass `headerReset` to also render the header's reset button.
 */
export function renderWithProviders(
  ui: ReactElement,
  {
    route = '/',
    queryClient = testQueryClient(),
    headerReset = false,
  }: { route?: string; queryClient?: QueryClient; headerReset?: boolean } = {},
) {
  const result = render(
    <QueryClientProvider client={queryClient}>
      <ProfileProvider>
        <FilterResetProvider>
          <MemoryRouter initialEntries={[route]}>
            {headerReset && <FilterResetButton />}
            {ui}
          </MemoryRouter>
        </FilterResetProvider>
      </ProfileProvider>
    </QueryClientProvider>,
  );
  return { ...result, queryClient };
}
