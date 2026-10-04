import { test as base, expect, type Locator, type Page } from '@playwright/test';
import { startServer, type ApiUser, type AppServer } from './server.ts';

/** Wednesday 16 September 2026, 10:00 in Amsterdam. */
export const NOW = '2026-09-16T08:00:00.000Z';
export const TODAY = '2026-09-16';

export const test = base.extend<{ app: AppServer }>({
  // eslint-disable-next-line no-empty-pattern -- Playwright reads fixture dependencies from an object pattern
  app: async ({}, use, testInfo) => {
    const app = await startServer({ now: NOW, database: `e2e_${testInfo.testId.replace(/\W/g, '')}_${Date.now()}` });
    await use(app);
    await app.stop();
  },
});

export { expect };

export const MOBILE = { viewport: { width: 375, height: 812 }, isMobile: true, hasTouch: true };

/** Opens the app with the browser clock on the server's fake now, optionally with a remembered profile. */
export async function openAs(page: Page, app: AppServer, profile: ApiUser | null, path = '/') {
  await page.clock.setFixedTime(new Date(app.now));
  if (profile) {
    await page.addInitScript((id) => window.localStorage.setItem('huishoudplanner.profileId', id), profile.id);
  }
  await page.goto(`${app.baseURL}${path}`);
}

export interface ApiTask {
  id: string;
  name: string;
}

export async function createTask(
  app: AppServer,
  as: ApiUser,
  input: { name: string; room: string; intervalKey: string; durationMinutes: number; defaultAssigneeId?: string | null },
): Promise<ApiTask> {
  const rooms = await app.list<{ id: string; name: string }>('/api/v2/rooms');
  const room = rooms.find((r) => r.name === input.room);
  if (!room) throw new Error(`no room named ${input.room}`);
  return app.api<ApiTask>('POST', '/api/v2/tasks', {
    as,
    body: {
      name: input.name,
      roomId: room.id,
      intervalKey: input.intervalKey,
      durationMinutes: input.durationMinutes,
      defaultAssigneeId: input.defaultAssigneeId ?? null,
    },
  });
}

/** Generates the current and next cycle (the scheduler is off in E2E). */
export async function generateCycles(app: AppServer, as: ApiUser) {
  await app.api('POST', '/api/v2/jobs/generation', { as });
}

/** Puts a task on a day as an ad-hoc occurrence (planned, not done; every call is its own request, so its own record). */
export async function planOn(app: AppServer, as: ApiUser, task: ApiTask, date: string, assignee: ApiUser | null) {
  return app.api<{ id: string }>('POST', '/api/v2/occurrences', {
    as,
    body: { taskId: task.id, date, assigneeId: assignee?.id ?? null, requestId: crypto.randomUUID() },
  });
}

export interface ApiOccurrence {
  id: string;
  taskId: string | null;
  taskNameSnapshot: string;
  roomNameSnapshot: string | null;
  date: string;
  assigneeId: string | null;
  status: 'open' | 'done' | 'skipped';
  completedBy: string | null;
  origin: 'generated' | 'adhoc';
  recordedDone: boolean;
}

export async function occurrencesOn(app: AppServer, date: string) {
  return app.list<ApiOccurrence>(`/api/v2/occurrences?from=${date}&to=${date}`);
}

/** The intent endpoints of an occurrence (they take no If-Match). */
export async function actOn(
  app: AppServer,
  as: ApiUser,
  occurrence: { id: string },
  action: 'complete' | 'uncomplete' | 'skip',
  body: Record<string, unknown> = {},
) {
  return app.api<ApiOccurrence>('POST', `/api/v2/occurrences/${occurrence.id}/${action}`, { as, body });
}

async function centre(locator: Locator) {
  let lastError: unknown;
  // A query refetch can replace a card between resolving the locator and scrolling it.
  // Re-resolve the locator instead of making drag tests depend on render timing.
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      await expect(locator).toBeVisible();
      await locator.scrollIntoViewIfNeeded();
      const box = await locator.boundingBox();
      if (box) return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
    } catch (error) {
      lastError = error;
    }
  }
  throw lastError ?? new Error('element has no bounding box');
}

/** Mouse drag in small steps, so dnd-kit's pointer sensor (5 px) activates and sees the target. */
export async function mouseDrag(page: Page, source: Locator, target: Locator) {
  let lastError: unknown;
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      const from = await centre(source);
      await page.mouse.move(from.x, from.y);
      await page.mouse.down();
      await page.mouse.move(from.x + 12, from.y + 12, { steps: 5 });
      await expect(source).toHaveClass(/opacity-60/, { timeout: 2_000 });
      const to = await centre(target);
      await page.mouse.move(to.x, to.y, { steps: 20 });
      await page.mouse.move(to.x + 1, to.y + 1);
      await expect(target).toHaveClass(/is-over/, { timeout: 2_000 });
      await page.mouse.up();
      return;
    } catch (error) {
      lastError = error;
      await page.mouse.up();
    }
  }
  throw lastError;
}

/** Touch drag through the Chrome DevTools protocol: hold (dnd-kit touch delay is 200 ms), then move. */
export async function touchDrag(page: Page, source: Locator, target: Locator) {
  const from = await centre(source);
  const to = await centre(target);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [from] });
  // Holding still is the gesture itself, not a wait for the app.
  await page.waitForTimeout(400);
  const steps = 15;
  for (let i = 1; i <= steps; i++) {
    await cdp.send('Input.dispatchTouchEvent', {
      type: 'touchMove',
      touchPoints: [{ x: from.x + ((to.x - from.x) * i) / steps, y: from.y + ((to.y - from.y) * i) / steps }],
    });
  }
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await cdp.detach();
}
