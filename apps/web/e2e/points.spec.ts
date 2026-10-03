import { createTask, expect, generateCycles, openAs, planOn, test, TODAY } from './fixtures.ts';
import type { ApiUser, AppServer } from './server.ts';

/** Wednesday 16 September 2026 is in the week of Monday 14 September. */
const WEEK = 'from=2026-09-14&to=2026-09-20';

async function pointsOf(app: AppServer, person: ApiUser): Promise<number | undefined> {
  const { balances } = await app.api<{ balances: { personId: string; points: number }[] }>(
    'GET',
    `/api/points/balances?${WEEK}`,
  );
  return balances.find((balance) => balance.personId === person._id)?.points;
}

test('points follow the person who did the work: on behalf, undo and take over', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const bram = await app.user('Bram');
  // 30 minutes earn 30 points and 20 minutes earn 20 points with the default of one point per minute.
  const stofzuigen = await createTask(app, anna, { name: 'Stofzuigen', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 30 });
  const dweilen = await createTask(app, anna, { name: 'Dweilen', room: 'Keuken', intervalKey: '1w', durationMinutes: 20 });
  await generateCycles(app, anna);
  await planOn(app, anna, stofzuigen, TODAY, anna);
  await planOn(app, anna, dweilen, TODAY, anna);
  await expect.poll(() => pointsOf(app, anna)).toBe(0);
  await expect.poll(() => pointsOf(app, bram)).toBe(0);

  // Bram checks off Anna's task on behalf of Anna: Anna receives the points.
  await openAs(page, app, bram, '/today');
  await page.getByLabel('Filter op persoon').selectOption('all');
  await page.getByRole('button', { name: 'Afvinken: Stofzuigen' }).click();
  await expect(page.getByRole('alertdialog')).toContainText('Deze taak staat op naam van Anna');
  await page.getByRole('button', { name: 'Namens Anna afvinken' }).click();
  const snackbar = page.locator('.snackbar');
  await expect(snackbar).toContainText('"Stofzuigen" afgevinkt.');
  await expect.poll(() => pointsOf(app, anna)).toBe(30);
  expect(await pointsOf(app, bram)).toBe(0);

  // Undo restores the balance.
  await snackbar.getByRole('button', { name: 'Ongedaan maken' }).click();
  await expect.poll(() => pointsOf(app, anna)).toBe(0);
  expect(await pointsOf(app, bram)).toBe(0);

  // Bram takes over another task of Anna: Bram receives the points.
  await page.getByRole('button', { name: 'Afvinken: Dweilen' }).click();
  await page.getByRole('button', { name: 'Ik heb de taak overgenomen' }).click();
  await expect.poll(() => pointsOf(app, bram)).toBe(20);
  expect(await pointsOf(app, anna)).toBe(0);

  // The statistics page shows the balances and the entries of the active profile for the week.
  await openAs(page, app, bram, '/manage/statistics');
  await page.getByRole('tab', { name: 'Punten' }).click();
  const balances = page.getByRole('table', { name: 'Punten per persoon' });
  await expect(balances.getByRole('row', { name: /Anna/ })).toContainText('0');
  await expect(balances.getByRole('row', { name: /Bram/ })).toContainText('20');
  const entries = page.getByRole('table', { name: 'Posten van Bram' });
  await expect(entries).toContainText('Dweilen');
  await expect(entries).toContainText('+20');
});
