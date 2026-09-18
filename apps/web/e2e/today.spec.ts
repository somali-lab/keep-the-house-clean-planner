import { createTask, expect, generateCycles, MOBILE, occurrencesOn, openAs, planOn, test, TODAY } from './fixtures.ts';

test.use(MOBILE);

test('check off, undo, skip with a reason, check off for someone else, and see both people in the history', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const bram = await app.user('Bram');
  const tasks = {
    afwassen: await createTask(app, anna, { name: 'Afwassen', room: 'Keuken', intervalKey: '1w', durationMinutes: 15 }),
    stofzuigen: await createTask(app, anna, { name: 'Stofzuigen', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 20 }),
    planten: await createTask(app, anna, { name: 'Planten water geven', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 5 }),
  };
  await generateCycles(app, anna);
  for (const task of Object.values(tasks)) await planOn(app, anna, task, TODAY, anna);
  const statusOf = async (taskId: string) => (await occurrencesOn(app, TODAY)).find((o) => o.taskId === taskId);

  await openAs(page, app, anna, '/mobile/today');
  const mine = page.getByRole('region', { name: 'Mijn taken vandaag' });
  const finished = page.getByRole('region', { name: 'Afgerond vandaag' });
  await expect(mine).toContainText('Afwassen');

  // Check off, then undo from the snackbar.
  await page.getByRole('button', { name: 'Afvinken: Afwassen' }).click();
  const snackbar = page.locator('.snackbar');
  await expect(snackbar).toContainText('"Afwassen" afgevinkt.');
  await expect.poll(async () => (await statusOf(tasks.afwassen._id))?.status).toBe('done');
  await snackbar.getByRole('button', { name: 'Ongedaan maken' }).click();
  await expect(mine).toContainText('Afwassen');
  await expect.poll(async () => (await statusOf(tasks.afwassen._id))?.status).toBe('open');

  // Skip with a reason.
  await page.getByRole('button', { name: 'Meer voor Stofzuigen' }).click();
  await page.getByRole('button', { name: 'Overslaan', exact: true }).click();
  await page.getByLabel('Reden (optioneel)').fill('geen tijd');
  await page.getByRole('button', { name: 'Overslaan bevestigen' }).click();
  await expect(finished).toContainText('Overgeslagen: geen tijd');

  // Check off on behalf of Bram.
  await page.getByRole('button', { name: 'Meer voor Planten water geven' }).click();
  await page.getByLabel('Afgevinkt door').selectOption({ label: 'Bram' });
  await page.getByRole('button', { name: 'Afvinken namens' }).click();
  await expect(finished).toContainText('Gedaan door Bram');
  await expect.poll(async () => (await statusOf(tasks.planten._id))?.completedBy).toBe(bram._id);

  await page.getByRole('button', { name: 'Instellingen en beheer openen' }).click();
  await page.getByRole('navigation', { name: 'Hoofdmenu' }).getByRole('link', { name: 'Geschiedenis' }).click();
  const history = page.locator('.history-list');
  await expect(history).toContainText('Anna vinkte Planten water geven in Woonkamer op 16-09-2026 af, gedaan door Bram');
  await expect(history).toContainText('Anna sloeg Stofzuigen in Woonkamer op 16-09-2026 over: "geen tijd"');
  await expect(history).toContainText('Anna maakte het afvinken van Afwassen in Keuken op 16-09-2026 ongedaan');
});
