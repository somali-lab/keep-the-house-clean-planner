import { createTask, expect, generateCycles, openAs, planOn, test, TODAY } from './fixtures.ts';

test.use({ viewport: { width: 1280, height: 900 } });

test('shows everyone side by side today only on wide screens', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const bram = await app.user('Bram');
  const afwassen = await createTask(app, anna, { name: 'Afwassen', room: 'Keuken', intervalKey: '1w', durationMinutes: 15 });
  const dweilen = await createTask(app, anna, { name: 'Dweilen', room: 'Keuken', intervalKey: '1w', durationMinutes: 10 });
  await generateCycles(app, anna);
  await planOn(app, anna, afwassen, TODAY, anna);
  await planOn(app, anna, dweilen, TODAY, bram);

  await openAs(page, app, anna, '/today');
  await page.getByLabel('Filter op persoon').selectOption({ label: 'Iedereen' });
  const mine = page.getByRole('region', { name: 'Mijn taken' });
  const others = page.getByRole('region', { name: 'Van anderen' });
  await expect(others).toContainText('Dweilen');
  let [mineBox, othersBox] = [await mine.boundingBox(), await others.boundingBox()];
  expect(othersBox!.x).toBeGreaterThan(mineBox!.x + mineBox!.width - 1);
  expect(Math.abs(othersBox!.y - mineBox!.y)).toBeLessThan(2);

  // Just below the wide breakpoint the groups stack in one column again.
  await page.setViewportSize({ width: 1000, height: 900 });
  [mineBox, othersBox] = [await mine.boundingBox(), await others.boundingBox()];
  expect(Math.abs(othersBox!.x - mineBox!.x)).toBeLessThan(2);
  expect(othersBox!.y).toBeGreaterThan(mineBox!.y + mineBox!.height - 1);
});

test('returns from management to the week overview with the Home button', async ({ page, app }) => {
  const anna = await app.user('Anna');

  await openAs(page, app, anna, '/');
  await page.getByRole('button', { name: 'Instellingen en beheer openen' }).click();
  await page.getByRole('navigation', { name: 'Hoofdmenu' }).getByRole('link', { name: 'Statistiek' }).click();
  await expect(page).toHaveURL(/\/manage\/statistics$/);
  const home = page.getByRole('button', { name: 'Home: naar het weekoverzicht' });
  await home.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Weekoverzicht' })).toBeVisible();
  await expect(page).toHaveURL(/\/$/);

  await page.goBack();
  await expect(page).toHaveURL(/\/manage\/statistics$/);
});
