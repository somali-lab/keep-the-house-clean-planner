import { expect, openAs, test } from './fixtures.ts';

test('first visit asks for a profile, the header shows it, and switching works', async ({ page, app }) => {
  await openAs(page, app, null);

  await expect(page.getByRole('heading', { name: 'Wie ben jij?' })).toBeVisible();
  await page.getByRole('button', { name: 'Anna' }).click();
  await expect(page.getByTestId('current-profile')).toHaveText('Anna');

  // Remembered on this device.
  await page.reload();
  await expect(page.getByTestId('current-profile')).toHaveText('Anna');

  await page.getByRole('button', { name: 'Wissel naar Bram' }).click();
  await expect(page.getByTestId('current-profile')).toHaveText('Bram');
  await expect(page.getByRole('button', { name: 'Wissel naar Bram' })).toHaveAttribute('aria-pressed', 'true');

  const bram = await app.user('Bram');
  expect(await page.evaluate(() => window.localStorage.getItem('huishoudplanner.profileId'))).toBe(bram._id);
});
