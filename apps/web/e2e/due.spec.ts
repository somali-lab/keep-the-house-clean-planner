import { createTask, expect, generateCycles, MOBILE, NOW, occurrencesOn, openAs, test, TODAY } from './fixtures.ts';

test.use(MOBILE);

test('a task not done for 1.5 times its interval is "Flink achter" and can be planned in', async ({ page, app }) => {
  const anna = await app.user('Anna');

  // Created 21 days ago with a 14-day interval: ratio exactly 1.5.
  await app.restart('2026-08-26T08:00:00.000Z');
  const task = await createTask(app, anna, { name: 'Oven schoonmaken', room: 'Keuken', intervalKey: '2wk', durationMinutes: 30 });
  await app.restart(NOW);
  await generateCycles(app, anna);

  await openAs(page, app, anna, '/mobile/due');
  const row = page.locator('.due-item', { hasText: 'Oven schoonmaken' });
  await expect(row).toContainText('Flink achter');
  await expect(row).toContainText('21 dagen geleden');

  await row.getByRole('button', { name: 'Oven schoonmaken inplannen' }).click();
  const form = page.getByRole('form', { name: 'Oven schoonmaken inplannen' });
  await expect(form.getByLabel('Datum')).toHaveValue(TODAY);
  await form.getByLabel('Wie').selectOption({ label: 'Anna' });
  await form.getByRole('button', { name: 'Inplannen bevestigen' }).click();

  await expect(row).toContainText('Staat nog open op wo 16 sep');
  await expect.poll(async () => (await occurrencesOn(app, TODAY)).map((o) => o.taskId)).toEqual([task._id]);

  await page.getByRole('navigation', { name: 'Hoofdmenu' }).getByRole('link', { name: 'Vandaag' }).click();
  await expect(page.getByRole('region', { name: 'Mijn taken vandaag' })).toContainText('Oven schoonmaken');
});
