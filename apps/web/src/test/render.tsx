import {
  DEFAULT_INTERVALS,
  type OccurrenceView,
  type Room,
  type Settings,
  type Task,
} from '@huishoudplanner/shared';
import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router';
import { ProfileProvider } from '../identity/index.ts';
import { testQueryClient } from './fixtures.ts';

const STAMP = '2026-09-14T08:00:00.000Z';

export function makeRoom(overrides: Partial<Room> & Pick<Room, '_id' | 'name'>): Room {
  return { sortOrder: 10, active: true, virtual: false, createdAt: STAMP, updatedAt: STAMP, ...overrides };
}

export function makeTask(overrides: Partial<Task> & Pick<Task, '_id' | 'name' | 'roomId'>): Task {
  return {
    intervalKey: '1w',
    durationMinutes: 15,
    defaultAssigneeId: null,
    active: true,
    notes: '',
    tags: [],
    lastCompletedAt: null,
    createdAt: STAMP,
    updatedAt: STAMP,
    ...overrides,
  };
}

export function makeOccurrence(overrides: Partial<OccurrenceView> & Pick<OccurrenceView, '_id'>): OccurrenceView {
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
    origin: 'generated',
    createdAt: STAMP,
    updatedAt: STAMP,
    isOverdue: false,
    movedFrom: null,
    ...overrides,
  };
}

export function makeSettings(overrides: Partial<Settings> = {}): Settings {
  return {
    cycleAnchorDate: '2026-09-14',
    weekStartsOn: 1,
    timezone: 'Europe/Amsterdam',
    vacationRanges: [],
    intervals: DEFAULT_INTERVALS,
    aiProvider: { type: 'none' },
    completionControl: 'circle',
    promoteThreshold: 2,
    dismissedPromotions: [],
    createdAt: STAMP,
    updatedAt: STAMP,
    ...overrides,
  };
}

/** Renders a page with query client, profile context and an in-memory router. */
export function renderWithProviders(
  ui: ReactElement,
  { route = '/', queryClient = testQueryClient() }: { route?: string; queryClient?: QueryClient } = {},
) {
  const result = render(
    <QueryClientProvider client={queryClient}>
      <ProfileProvider>
        <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
      </ProfileProvider>
    </QueryClientProvider>,
  );
  return { ...result, queryClient };
}
