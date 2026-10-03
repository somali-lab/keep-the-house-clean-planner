import type { BrowserContext, Page } from '@playwright/test';
import { createTask, expect, generateCycles, planOn, test, TODAY } from './fixtures.ts';
import type { ApiUser, AppServer } from './server.ts';

/** 09:59:30 in Amsterdam on the server's fake day; the configured moment is 10:00. */
const BEFORE_MOMENT = new Date('2026-09-16T07:59:30.000Z');
const SHOWN_KEY = 'e2e.notifications';

/**
 * Replaces the Notification API in every page of the context. Shown notifications are
 * written to localStorage, which all pages of the context share, so they can be counted
 * across tabs.
 */
async function stubNotifications(context: BrowserContext, profile: ApiUser) {
  await context.addInitScript(
    ({ shownKey, profileId }) => {
      window.localStorage.setItem('huishoudplanner.profileId', profileId);
      class FakeNotification {
        static permission = 'granted';
        static requestPermission() {
          return Promise.resolve('granted');
        }
        constructor(title: string, options?: { body?: string; tag?: string }) {
          const shown = JSON.parse(window.localStorage.getItem(shownKey) ?? '[]') as unknown[];
          shown.push({ title, body: options?.body, tag: options?.tag });
          window.localStorage.setItem(shownKey, JSON.stringify(shown));
        }
      }
      Object.defineProperty(window, 'Notification', { configurable: true, writable: true, value: FakeNotification });
    },
    { shownKey: SHOWN_KEY, profileId: profile._id },
  );
}

const shownNotifications = (page: Page) =>
  page.evaluate((key) => JSON.parse(window.localStorage.getItem(key) ?? '[]') as { title: string; body: string }[], SHOWN_KEY);

const claimKeys = (page: Page) =>
  page.evaluate(() =>
    Object.keys(window.localStorage).filter((key) => key.startsWith('khc.notified.')),
  );

/** Opens the Today view in a page whose clock runs from just before the notification moment. */
async function openToday(page: Page, app: AppServer) {
  await page.clock.install({ time: BEFORE_MOMENT });
  await page.goto(`${app.baseURL}/today`);
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
}

async function configureMoment(app: AppServer, person: ApiUser) {
  await app.api('PUT', `/api/users/${person._id}/browser-notifications`, {
    as: person,
    body: { enabled: true, times: ['10:00'] },
  });
}

test('two open tabs show one notification for a configured moment', async ({ page, context, app }) => {
  const anna = await app.user('Anna');
  const afwassen = await createTask(app, anna, { name: 'Afwassen', room: 'Keuken', intervalKey: '1w', durationMinutes: 15 });
  const stofzuigen = await createTask(app, anna, { name: 'Stofzuigen', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 20 });
  await generateCycles(app, anna);
  await planOn(app, anna, afwassen, TODAY, anna);
  await planOn(app, anna, stofzuigen, TODAY, anna);
  await configureMoment(app, anna);
  await stubNotifications(context, anna);

  const second = await context.newPage();
  await openToday(page, app);
  await openToday(second, app);
  await expect(page.getByRole('region', { name: 'Mijn taken' })).toContainText('Afwassen');
  await expect(second.getByRole('region', { name: 'Mijn taken' })).toContainText('Afwassen');
  expect(await shownNotifications(page)).toEqual([]);

  // Both tabs reach 10:00 at the same time.
  await Promise.all([page.clock.fastForward(60_000), second.clock.fastForward(60_000)]);

  await expect.poll(async () => (await shownNotifications(page)).length).toBe(1);
  const [notification] = await shownNotifications(page);
  expect(notification).toMatchObject({
    title: 'Keep the House Clean: 2 taken vandaag',
    body: 'Afwassen, Stofzuigen',
  });
  expect(await claimKeys(second)).toEqual([`khc.notified.${anna._id}.${TODAY}.10:00`]);

  // Later checks inside the grace period, in either tab, do not repeat it.
  await Promise.all([page.clock.fastForward(120_000), second.clock.fastForward(120_000)]);
  // Not waiting for the app: this gives a duplicate, if there were one, time to show up.
  await page.waitForTimeout(500);
  expect(await shownNotifications(second)).toHaveLength(1);
});

test('an empty day shows no notification', async ({ page, context, app }) => {
  const anna = await app.user('Anna');
  await generateCycles(app, anna);
  await configureMoment(app, anna);
  await stubNotifications(context, anna);

  const second = await context.newPage();
  await openToday(page, app);
  await openToday(second, app);

  await Promise.all([page.clock.fastForward(60_000), second.clock.fastForward(60_000)]);

  // The moment was handled (claimed) but there was nothing to report.
  await expect.poll(() => claimKeys(page)).toEqual([`khc.notified.${anna._id}.${TODAY}.10:00`]);
  await page.waitForTimeout(500);
  expect(await shownNotifications(page)).toEqual([]);
  expect(await shownNotifications(second)).toEqual([]);
});
