import { actOn, createTask, expect, generateCycles, occurrencesOn, openAs, planOn, test, TODAY } from './fixtures.ts';

test('an administrator can correct and remove an incorrect completion', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const bram = await app.user('Bram');
  const task = await createTask(app, anna, {
    name: 'Kattenmandjes',
    room: 'Keuken',
    intervalKey: '1w',
    durationMinutes: 20,
    defaultAssigneeId: anna.id,
  });
  await generateCycles(app, anna);
  const occurrence = await planOn(app, anna, task, TODAY, anna);
  await actOn(app, anna, occurrence, 'complete');

  await openAs(page, app, anna, '/manage/completions');
  const row = page.getByRole('listitem').filter({ hasText: 'Kattenmandjes' });
  await expect(row).toContainText('Anna');
  await row.getByRole('button', { name: 'Kattenmandjes bewerken' }).click();

  const edit = page.getByRole('dialog', { name: 'Kattenmandjes bewerken' });
  await edit.getByLabel('Uitgevoerd door').selectOption(bram.id);
  await edit.getByRole('button', { name: 'Opslaan' }).click();
  await expect(page.getByRole('status')).toHaveText('De gereedmelding is bijgewerkt.');
  await expect(row).toContainText('Bram');

  await row.getByRole('button', { name: 'Gereedmelding van Kattenmandjes verwijderen' }).click();
  const remove = page.getByRole('dialog', { name: 'Kattenmandjes verwijderen?' });
  await expect(remove).toContainText('Ook deze geplande taakinstantie verdwijnt');
  await remove.getByRole('button', { name: 'Definitief verwijderen' }).click();

  await expect(row).toBeHidden();
  await expect.poll(async () => (await occurrencesOn(app, TODAY)).some((item) => item.id === occurrence.id)).toBe(false);
});

test('the sidebar icon for a long nav label stays visible instead of being squeezed to zero width', async ({
  page,
  app,
}) => {
  const anna = await app.user('Anna');
  await openAs(page, app, anna, '/manage/completions');
  const icon = page.locator('nav[aria-label="Hoofdmenu"] a[href="/manage/completions"] svg');
  const box = await icon.boundingBox();
  expect(box?.width).toBeGreaterThan(0);
});