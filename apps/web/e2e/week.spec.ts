import { createTask, expect, generateCycles, MOBILE, occurrencesOn, openAs, planOn, test, TODAY, touchDrag } from './fixtures.ts';

test.use(MOBILE);

test('drag an occurrence to another day by touch and see where it came from', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const task = await createTask(app, anna, { name: 'Bed verschonen', room: 'Slaapkamer', intervalKey: '1w', durationMinutes: 15 });
  await generateCycles(app, anna);
  await planOn(app, anna, task, TODAY, anna);

  await openAs(page, app, anna, '/');
  const wednesday = page.getByTestId(`day:${TODAY}`);
  const thursday = page.getByTestId('day:2026-09-17');
  const handle = wednesday.locator('.drag-handle', { hasText: 'Bed verschonen' });
  await expect(handle).toBeVisible();

  await touchDrag(page, handle, thursday);

  await expect(thursday).toContainText('Bed verschonen');
  await expect(thursday).toContainText('verplaatst van wo 16 sep');
  await expect(wednesday).not.toContainText('Bed verschonen');
  await expect.poll(async () => (await occurrencesOn(app, '2026-09-17')).map((o) => o.taskId)).toEqual([task.id]);
});

test('keeps task search and cycle-week choice after a hard reload', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const task = await createTask(app, anna, { name: 'Étage dweilen', room: 'Slaapkamer', intervalKey: '1w', durationMinutes: 15 });
  await generateCycles(app, anna);
  await planOn(app, anna, task, TODAY, anna);

  await openAs(page, app, anna, '/');
  await page.getByRole('textbox', { name: 'Zoek taken' }).fill('etage');
  await expect(page.getByTestId(`day:${TODAY}`)).toContainText('Étage dweilen');
  await page.getByRole('button', { name: 'Toon cyclusweek' }).click();
  await expect(page.getByTestId(`day:${TODAY}`)).toContainText('Cyclusweek 1');

  await page.reload();
  await expect(page.getByRole('textbox', { name: 'Zoek taken' })).toHaveValue('etage');
  await expect(page.getByRole('button', { name: 'Toon cyclusweek' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId(`day:${TODAY}`)).toContainText('Étage dweilen');
  await page.getByRole('button', { name: 'Filters van dit scherm resetten' }).click();
  await expect(page.getByRole('textbox', { name: 'Zoek taken' })).toHaveValue('');
});
