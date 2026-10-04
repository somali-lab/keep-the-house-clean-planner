import { expect, test } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { startServer, type ApiUser, type AppServer } from './server.ts';

/**
 * Regenerates the README screenshots from a scripted demo household, so the
 * images can be reproduced instead of being captured by hand. Run them with
 * `npm run screenshots` after a UI change that the README shows.
 */

/** Monday 14 September 2026; the cycle starts here, so the week has a history. */
const CYCLE_START = '2026-09-14T06:30:00.000Z';
/** Wednesday 16 September 2026, 10:00 in Amsterdam: the day the screenshots show. */
const NOW = '2026-09-16T08:00:00.000Z';
const OUTPUT_DIR = resolve(import.meta.dirname, '../../../docs/screenshots');
const CYCLE_DAYS = 28;

/** The seeded rooms are Dutch; the published screenshots are English. */
const ROOM_NAMES: Record<string, string> = {
  Keuken: 'Kitchen',
  Badkamer: 'Bathroom',
  Toilet: 'Toilet',
  Woonkamer: 'Living room',
  Slaapkamer: 'Bedroom',
  Hal: 'Hallway',
  'Hele huis': 'Whole house',
};

interface DemoTask {
  name: string;
  room: string;
  intervalKey: string;
  durationMinutes: number;
  /** Day of the cycle the first slot lands on; keeps chores off each other's days. */
  offset: number;
  /** Chores nobody owns end up in the "Anyone" row. */
  unassigned?: boolean;
  /** Left unplanned, so the planner screenshot shows the pool with work to distribute. */
  inPool?: boolean;
}

const DEMO_TASKS: DemoTask[] = [
  { name: 'Do the dishes', room: 'Kitchen', intervalKey: 'daily', durationMinutes: 15, offset: 0 },
  { name: 'Wipe the worktop', room: 'Kitchen', intervalKey: '3w', durationMinutes: 10, offset: 1 },
  {
    name: 'Mop the kitchen floor',
    room: 'Kitchen',
    intervalKey: '1w',
    durationMinutes: 20,
    offset: 5,
  },
  { name: 'Clean the oven', room: 'Kitchen', intervalKey: '4wk', durationMinutes: 30, offset: 13 },
  { name: 'Clean the shower', room: 'Bathroom', intervalKey: '1w', durationMinutes: 20, offset: 2 },
  {
    name: 'Scrub the washbasin',
    room: 'Bathroom',
    intervalKey: '2w',
    durationMinutes: 10,
    offset: 4,
  },
  {
    name: 'Descale the taps',
    room: 'Bathroom',
    intervalKey: '4wk',
    durationMinutes: 25,
    offset: 20,
    inPool: true,
  },
  { name: 'Clean the toilet', room: 'Toilet', intervalKey: '2w', durationMinutes: 10, offset: 1 },
  {
    name: 'Vacuum the living room',
    room: 'Living room',
    intervalKey: '2w',
    durationMinutes: 15,
    offset: 3,
  },
  {
    name: 'Dust the shelves',
    room: 'Living room',
    intervalKey: '1w',
    durationMinutes: 15,
    offset: 4,
  },
  {
    name: 'Water the plants',
    room: 'Living room',
    intervalKey: '3w',
    durationMinutes: 5,
    offset: 0,
  },
  {
    name: 'Change the bedding',
    room: 'Bedroom',
    intervalKey: '2wk',
    durationMinutes: 15,
    offset: 6,
  },
  {
    name: 'Vacuum the bedroom',
    room: 'Bedroom',
    intervalKey: '1w',
    durationMinutes: 10,
    offset: 6,
  },
  {
    name: 'Vacuum the hallway',
    room: 'Hallway',
    intervalKey: '1w',
    durationMinutes: 10,
    offset: 3,
  },
  {
    name: 'Do the laundry',
    room: 'Whole house',
    intervalKey: '3w',
    durationMinutes: 20,
    offset: 2,
  },
  {
    name: 'Take out the rubbish',
    room: 'Whole house',
    intervalKey: '2w',
    durationMinutes: 5,
    offset: 2,
    unassigned: true,
  },
];

const WEEK_THEMES: [string, string, string, string] = [
  'Kitchen week',
  'Bathroom week',
  'Living room week',
  'Bedroom week',
];

interface ApiRoom {
  id: string;
  name: string;
}

interface ApiTask {
  id: string;
}

interface ApiPlan {
  id: string;
  active: boolean;
  version: number;
}

interface ApiInterval {
  key: string;
  /** How often the task is planned in one cycle; the settings answer it per interval. */
  perCycle: number | null;
}

interface ApiOccurrence {
  id: string;
  assigneeId: string | null;
  status: string;
}

interface Slot {
  taskId: string;
  weekIndex: number;
  weekday: number;
  assigneeId: string | null;
  sortOrder: number;
}

/** Spreads the required slots evenly over the cycle, starting at `offset`. */
function cyclePositions(count: number, offset: number): number[] {
  return Array.from(
    { length: count },
    (_, index) => (Math.round((index * CYCLE_DAYS) / count) + offset) % CYCLE_DAYS,
  );
}

function slotsFor(
  task: DemoTask,
  taskId: string,
  taskIndex: number,
  people: ApiUser[],
  intervals: ApiInterval[],
): Slot[] {
  if (task.inPool) return [];
  const required = intervals.find((interval) => interval.key === task.intervalKey)?.perCycle;
  if (!required) throw new Error(`interval ${task.intervalKey} is not planned on the grid`);
  return cyclePositions(required, task.offset).map((position, index) => ({
    taskId,
    weekIndex: Math.floor(position / 7),
    // Cycle positions are Monday-first, while weekday 0 is Sunday.
    weekday: ((position % 7) + 1) % 7,
    assigneeId: task.unassigned ? null : people[(taskIndex + index) % people.length]!.id,
    sortOrder: index,
  }));
}

async function buildHousehold(app: AppServer, people: ApiUser[]) {
  const admin = people[0]!;

  for (const person of people) {
    await app.edit(
      `/api/v2/users/${person.id}`,
      { dailyBudgetMinutes: { weekday: 150, weekend: 90 } },
      { as: admin },
    );
  }

  const rooms = await app.list<ApiRoom>('/api/v2/rooms');
  const { intervals } = await app.api<{ intervals: ApiInterval[] }>('GET', '/api/v2/settings');
  const roomIdByName = new Map<string, string>();
  for (const room of rooms) {
    const english = ROOM_NAMES[room.name];
    if (!english) continue;
    await app.edit(`/api/v2/rooms/${room.id}`, { name: english }, { as: admin });
    roomIdByName.set(english, room.id);
  }

  const slots: Slot[] = [];
  for (const [index, demo] of DEMO_TASKS.entries()) {
    const roomId = roomIdByName.get(demo.room);
    if (!roomId) throw new Error(`no room named ${demo.room}`);
    const task = await app.api<ApiTask>('POST', '/api/v2/tasks', {
      as: admin,
      body: {
        name: demo.name,
        roomId,
        intervalKey: demo.intervalKey,
        durationMinutes: demo.durationMinutes,
        defaultAssigneeId: null,
      },
    });
    slots.push(...slotsFor(demo, task.id, index, people, intervals));
  }

  const plan = (await app.list<ApiPlan>('/api/v2/cycle-plans')).find((item) => item.active);
  if (!plan) throw new Error('the seeded plan is not active');
  const renamed = await app.edit<ApiPlan>(
    `/api/v2/cycle-plans/${plan.id}`,
    { name: 'Standard', weekThemes: WEEK_THEMES },
    { as: admin },
  );
  await app.api('PUT', `/api/v2/cycle-plans/${plan.id}/slots`, {
    as: admin,
    body: { slots },
    ifMatch: renamed.version,
  });
  await app.api('POST', '/api/v2/jobs/generation', { as: admin });
}

/** Checks off that day's chores as the person they were planned for. */
async function completeDay(app: AppServer, people: ApiUser[], day: string, leaveOpen = 0) {
  const occurrences = await app.list<ApiOccurrence>(`/api/v2/occurrences?from=${day}&to=${day}`);
  const open = occurrences.filter(
    (occurrence) => occurrence.status === 'open' && occurrence.assigneeId !== null,
  );
  for (const occurrence of open.slice(0, Math.max(0, open.length - leaveOpen))) {
    const assignee = people.find((person) => person.id === occurrence.assigneeId);
    if (!assignee) continue;
    await app.api('POST', `/api/v2/occurrences/${occurrence.id}/complete`, {
      as: assignee,
      body: {},
    });
  }
}

test('capture the README screenshots', async ({ page }) => {
  const app = await startServer({ now: CYCLE_START, database: `screenshots_${Date.now()}` });
  try {
    const people = [await app.user('Anna'), await app.user('Bram')];
    await buildHousehold(app, people);

    // Live through the first days of the cycle, so the overview has a real history.
    await completeDay(app, people, '2026-09-14');
    await app.restart('2026-09-15T06:30:00.000Z');
    await completeDay(app, people, '2026-09-15', 1);
    await app.restart(NOW);
    await completeDay(app, people, '2026-09-16', 4);

    await mkdir(OUTPUT_DIR, { recursive: true });
    await page.clock.setFixedTime(new Date(app.now));
    await page.addInitScript((profileId) => {
      window.localStorage.setItem('huishoudplanner.profileId', profileId);
      window.localStorage.setItem('huishoudplanner.language', 'en');
      window.localStorage.setItem('huishoudplanner.theme', 'light');
    }, people[0]!.id);

    await page.goto(`${app.baseURL}/`);
    await page.getByLabel('Filter by person').selectOption('all');
    await expect(page.getByTestId('week-day-grid')).toContainText('Do the dishes');
    await page.screenshot({ path: resolve(OUTPUT_DIR, 'week-overview.png') });

    await page.goto(`${app.baseURL}/manage/planner`);
    await expect(page.getByText('Descale the taps').first()).toBeVisible();
    await page.screenshot({ path: resolve(OUTPUT_DIR, 'four-week-planner.png') });

    await page.goto(`${app.baseURL}/manage/distribution`);
    await expect(page.getByText('Week 1', { exact: true }).first()).toBeVisible();
    await page.screenshot({ path: resolve(OUTPUT_DIR, 'work-distribution.png') });
  } finally {
    await app.stop();
  }
});
