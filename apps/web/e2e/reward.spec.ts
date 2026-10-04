import { actOn, createTask, expect, generateCycles, occurrencesOn, openAs, planOn, test, TODAY } from './fixtures.ts';
import type { ApiUser, AppServer } from './server.ts';

/** The browser remembers the animation of a person, a period and its first day: Monday 14 September 2026 for the week. */
const celebrated = (page: import('@playwright/test').Page, person: ApiUser) =>
  page.evaluate((id) => window.localStorage.getItem(`khc.rewardCelebrated.${id}.week.2026-09-14`), person.id);

/** Anna has three tasks of 3 points today (3 minutes each); the goal she has without an explicit one is 9 points. */
async function planThree(app: AppServer) {
  const anna = await app.user('Anna');
  await generateCycles(app, anna);
  for (const [name, room] of [['Stofzuigen', 'Woonkamer'], ['Dweilen', 'Keuken'], ['Ramen lappen', 'Woonkamer']] as const) {
    const task = await createTask(app, anna, { name, room, intervalKey: '1w', durationMinutes: 3 });
    await planOn(app, anna, task, TODAY, anna);
  }
  return anna;
}

/** The same, with an explicit goal of 6 points for the week. */
async function setup(app: AppServer) {
  const anna = await planThree(app);
  await app.edit('/api/v2/settings', { rewardGoals: { weekPoints: 6, cyclePoints: null } }, { as: anna });
  return anna;
}

test('an administrator sets a goal, completing tasks fills the meter, the celebration plays once and a reload does not replay it', async ({ page, app }) => {
  const anna = await planThree(app);

  // Without a goal the meter follows the planned work: three tasks of 3 points are 9 points.
  await openAs(page, app, anna, '/reward');
  await expect(page.getByRole('heading', { level: 1, name: 'Beloning' })).toBeVisible();
  await expect(page.getByText('0 van 9 punten (0%)')).toBeVisible();
  await expect(page.getByText('Het doel is het totaal van de punten van het werk dat voor je gepland staat.')).toBeVisible();

  // The administrator sets a goal of 6 points for the week in the settings; the calendar tab is the first one.
  await openAs(page, app, anna, '/manage/settings');
  const card = page.getByRole('form', { name: 'Beloningsdoelen' });
  await card.getByLabel('Doel per week (punten)').fill('6');
  await card.getByRole('button', { name: 'Doelen opslaan' }).click();
  await expect(card.getByRole('status')).toHaveText('Opgeslagen.');
  const settings = await app.api<{ rewardGoals: unknown }>('GET', '/api/v2/settings');
  expect(settings.rewardGoals).toEqual({ weekPoints: 6, cyclePoints: null });

  // Completing the first task fills half of the meter: 3 of 6 points, five eggs and a progress bar at 50%.
  await openAs(page, app, anna, '/today');
  await page.getByRole('button', { name: 'Afvinken: Stofzuigen' }).click();
  await expect(page.locator('.snackbar')).toContainText('"Stofzuigen" afgevinkt.');
  await page.getByRole('link', { name: 'Beloning' }).click();
  await expect(page.getByText('3 van 6 punten (50%)')).toBeVisible();
  await expect(page.getByRole('progressbar', { name: 'Voortgang deze week' })).toHaveAttribute('aria-valuenow', '50');
  await expect(page.getByText('5 van 10 eieren in de mand')).toBeVisible();
  await expect(page.getByText('Het doel is ingesteld door een beheerder.')).toBeVisible();
  await expect(page.getByText('Doel gehaald!')).toHaveCount(0);
  expect(await celebrated(page, anna)).toBeNull();

  // The second task fills the meter: the goal is met and the animation plays.
  await page.getByRole('link', { name: 'Vandaag' }).click();
  await page.getByRole('button', { name: 'Afvinken: Dweilen' }).click();
  await expect(page.locator('.snackbar')).toContainText('"Dweilen" afgevinkt.');
  await page.getByRole('link', { name: 'Beloning' }).click();
  await expect(page.getByText('6 van 6 punten (100%)')).toBeVisible();
  await expect(page.getByText('10 van 10 eieren in de mand')).toBeVisible();
  await expect(page.getByText('Doel gehaald!')).toBeVisible();
  const scene = page.getByRole('img', { name: /Een kip loopt naar de mand/ });
  await expect(scene).toHaveAttribute('data-celebrating', 'true');
  expect(await celebrated(page, anna)).toBe('1');

  // The chicken stands at the end of the track, moved there with a transform.
  await expect(page.getByTestId('reward-chicken')).toHaveAttribute('data-position', '100');
  await expect(page.getByTestId('reward-chicken')).toHaveCSS('transform', /matrix\(1, 0, 0, 1, 190, 0\)/);

  // A reload shows the same progress and the message, and never plays the animation again.
  await page.reload();
  await expect(page.getByText('6 van 6 punten (100%)')).toBeVisible();
  await expect(page.getByText('Doel gehaald!')).toBeVisible();
  await expect(page.getByRole('img', { name: /Een kip loopt naar de mand/ })).toHaveAttribute('data-celebrating', 'false');

  // The cycle has its own, automatic goal (all three tasks, 9 points) and its own celebration; the toggle is remembered after a reload.
  await page.getByRole('button', { name: 'Cyclus' }).click();
  await expect(page.getByText('6 van 9 punten (66%)')).toBeVisible();
  await expect(page.getByRole('progressbar', { name: 'Voortgang deze cyclus' })).toHaveAttribute('aria-valuenow', '66');
  await expect(page.getByText('Doel gehaald!')).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole('button', { name: 'Cyclus' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByText('6 van 9 punten (66%)')).toBeVisible();
});

test('with reduced motion the meter plays no animation and says the goal was met as text', async ({ page, app }) => {
  const anna = await setup(app);
  for (const occurrence of (await occurrencesOn(app, TODAY)).slice(0, 2)) {
    await actOn(app, anna, occurrence, 'complete');
  }
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openAs(page, app, anna, '/reward');

  await expect(page.getByText('6 van 6 punten (100%)')).toBeVisible();
  await expect(page.getByText('Doel gehaald!')).toBeVisible();
  const scene = page.getByRole('img', { name: /Een kip loopt naar de mand/ });
  await expect(scene).toHaveAttribute('data-celebrating', 'false');
  // The chicken is in place at once and nothing is remembered, since nothing played.
  await expect(page.getByTestId('reward-chicken')).toHaveAttribute('data-position', '100');
  await expect(page.getByTestId('reward-chicken')).toHaveCSS('transition-duration', '0s');
  expect(await celebrated(page, anna)).toBeNull();
  await expect(page.getByRole('progressbar', { name: 'Voortgang deze week' })).toHaveAttribute('aria-valuenow', '100');
});

test('the badges of the person are shown on the tab', async ({ page, app }) => {
  const anna = await setup(app);
  await app.api('POST', '/api/v2/badges', { as: anna, body: { name: 'Stofzuigkoning', rule: { type: 'executions', taskIds: [], threshold: 1 } } });
  const occurrences = await occurrencesOn(app, TODAY);
  await actOn(app, anna, occurrences[0]!, 'complete');
  await openAs(page, app, anna, '/reward');
  const badges = page.getByRole('region', { name: 'Mijn badges' });
  await expect(badges).toContainText('Stofzuigkoning');
  await expect(badges).toContainText('Behaald op 16 september 2026');
  await expect(page.getByText('3 van 6 punten (50%)')).toBeVisible();
});

for (const width of [320, 375]) {
  test(`the five overview tabs fit at ${width} px and the reward tab is reachable by keyboard`, async ({ page, app }, testInfo) => {
    const anna = await setup(app);
    await page.setViewportSize({ width, height: 700 });
    await openAs(page, app, anna, '/');
    const nav = page.getByRole('navigation', { name: 'Hoofdmenu' });
    const links = nav.getByRole('link');
    await expect(links).toHaveText(['Week', 'Vandaag', 'Taken', 'Achterstand', 'Beloning']);

    // Every tab lies inside the screen, no text is clipped and the page does not scroll sideways.
    const fits = await links.evaluateAll((elements, viewport) =>
      elements.map((element) => {
        const box = element.getBoundingClientRect();
        return {
          label: element.textContent,
          inside: box.left >= 0 && box.right <= viewport,
          clipped: element.scrollWidth > element.clientWidth + 1,
          // The label is one line: its text has a single client rectangle.
          lines: (() => {
            const range = document.createRange();
            range.selectNodeContents(element.lastChild!);
            return range.getClientRects().length;
          })(),
        };
      }),
      width,
    );
    for (const fit of fits) expect(fit, fit.label ?? '').toMatchObject({ inside: true, clipped: false, lines: 1 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`overview-${width}.png`) });

    // The keyboard reaches the tab and Enter opens it.
    await nav.getByRole('link', { name: 'Achterstand' }).focus();
    await page.keyboard.press('Tab');
    await expect(nav.getByRole('link', { name: 'Beloning' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { level: 1, name: 'Beloning' })).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Beloning' })).toHaveAttribute('aria-current', 'page');
    await expect(page.getByText('0 van 6 punten (0%)')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`reward-${width}.png`), fullPage: true });
  });
}
