import { createTask, expect, generateCycles, openAs, planOn, test, TODAY } from './fixtures.ts';

/** Monday 21 September 2026, 03:00 in Amsterdam: the week of Monday 14 September ended at midnight. */
const NEXT_MONDAY = '2026-09-21T01:00:00.000Z';

test('an administrator sets the bonuses; after the week ends the Points tab shows the week bonuses of the person who did everything', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const bram = await app.user('Bram');
  const stofzuigen = await createTask(app, anna, { name: 'Stofzuigen', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 30 });
  const dweilen = await createTask(app, anna, { name: 'Dweilen', room: 'Keuken', intervalKey: '1w', durationMinutes: 20 });
  const afwassen = await createTask(app, anna, { name: 'Afwassen', room: 'Keuken', intervalKey: '1w', durationMinutes: 10 });
  const ramen = await createTask(app, anna, { name: 'Ramen lappen', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 15 });
  await generateCycles(app, anna);

  // The administrator turns on the week bonuses in the settings.
  await openAs(page, app, anna, '/manage/settings');
  const card = page.getByRole('form', { name: 'Bonussen' });
  await expect(card).toBeVisible();
  await card.getByLabel('Week: alles gedaan').fill('5');
  await card.getByLabel('Week: alles op tijd').fill('3');
  await card.getByRole('button', { name: 'Bonussen opslaan' }).click();
  await expect(card.getByRole('status')).toHaveText('Opgeslagen.');
  await expect(card.getByText('Deze bedragen gelden sinds 16-09-2026.')).toBeVisible();
  const settings = await app.api<{ bonusSchedule: unknown[] }>('GET', '/api/settings');
  expect(settings.bonusSchedule).toEqual([{ from: TODAY, weekDone: 5, weekOnTime: 3, cycleDone: 0, cycleOnTime: 0 }]);

  // Anna finishes everything planned for her this week.
  for (const task of [stofzuigen, dweilen]) {
    const occurrence = await planOn(app, anna, task, TODAY, anna);
    await app.api('PATCH', `/api/occurrences/${occurrence._id}`, { as: anna, body: { action: 'complete' } });
  }
  // Bram does one task and skips the other, so he has not done everything.
  const done = await planOn(app, anna, ramen, TODAY, bram);
  await app.api('PATCH', `/api/occurrences/${done._id}`, { as: bram, body: { action: 'complete' } });
  const skipped = await planOn(app, anna, afwassen, TODAY, bram);
  await app.api('PATCH', `/api/occurrences/${skipped._id}`, { as: bram, body: { action: 'skip', reason: 'Geen tijd' } });

  // While the week is running nothing is paid, however complete it is already.
  const balancesOf = async () =>
    (await app.api<{ balances: { personId: string; points: number; bonusPoints: number }[] }>('GET', '/api/points/balances?from=2026-09-14&to=2026-09-20')).balances;
  expect((await balancesOf()).map((balance) => balance.bonusPoints)).toEqual([0, 0]);

  // The server restarts on Monday 03:00 with a fixed clock; the reconciliation at startup finalises the week.
  await app.restart(NEXT_MONDAY);
  const annaBalance = (await balancesOf()).find((balance) => balance.personId === anna._id);
  expect(annaBalance).toMatchObject({ bonusPoints: 8, points: 30 + 20 + 8 });

  await openAs(page, app, anna, '/manage/statistics');
  await page.getByRole('tab', { name: 'Punten' }).click();
  await page.getByLabel('Periode').selectOption('weeks:3');

  const balances = page.getByRole('table', { name: 'Punten per persoon' });
  await expect(balances.getByRole('columnheader', { name: 'Bonus' })).toBeVisible();
  await expect(balances.getByRole('row', { name: /Anna/ }).getByRole('cell').nth(2)).toHaveText('8');
  await expect(balances.getByRole('row', { name: /Bram/ }).getByRole('cell').nth(2)).toHaveText('0');

  const annaEntries = page.getByRole('table', { name: 'Posten van Anna' });
  await expect(annaEntries.getByRole('row', { name: /Weekbonus: alles gedaan, week 38/ })).toContainText('+5');
  await expect(annaEntries.getByRole('row', { name: /Weekbonus: alles op tijd, week 38/ })).toContainText('+3');
  await expect(annaEntries.getByText(/Weekbonus/)).toHaveCount(2);

  await page.getByLabel('Toon posten van').selectOption({ label: 'Bram' });
  const bramEntries = page.getByRole('table', { name: 'Posten van Bram' });
  await expect(bramEntries).toContainText('Ramen lappen');
  await expect(bramEntries).not.toContainText('Weekbonus');
});
