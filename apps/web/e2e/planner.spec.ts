import { expect, mouseDrag, openAs, test } from './fixtures.ts';

interface ApiPlan {
  _id: string;
  active: boolean;
  slots: { taskId: string; weekIndex: number; weekday: number; assigneeId: string | null }[];
}

test('create a task, place it by dragging, get refused on an unavailable day, and activate the plan', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const bram = await app.user('Bram');
  // Bram cannot do Tuesdays (0 = Sunday … 6 = Saturday).
  await app.api('PATCH', `/api/users/${bram._id}`, { as: anna, body: { unavailableWeekdays: [2] } });

  await openAs(page, app, anna, '/tasks');
  await page.getByRole('button', { name: 'Nieuwe taak' }).click();
  const form = page.getByRole('form', { name: 'Nieuwe taak' });
  await form.getByLabel('Naam').fill('Ramen zemen');
  await form.getByLabel('Ruimte').selectOption({ label: 'Woonkamer' });
  await form.getByLabel('Interval').selectOption({ label: '1x per 4 weken' });

  // Duration is required.
  await form.getByRole('button', { name: 'Opslaan' }).click();
  await expect(form.getByRole('alert')).toHaveText('Vul de duur in minuten in.');
  await form.getByLabel('Duur (minuten)').fill('20');
  await form.getByRole('button', { name: 'Opslaan' }).click();
  await expect(form).toBeHidden();
  await expect(page.getByRole('region', { name: 'Woonkamer' })).toContainText('Ramen zemen');

  const task = (await app.api<{ _id: string; name: string }[]>('GET', '/api/tasks')).find((t) => t.name === 'Ramen zemen')!;

  await page.getByRole('navigation', { name: 'Hoofdmenu' }).getByRole('link', { name: 'Planner' }).click();
  // Work on a copy, so there is something to activate.
  await page.getByRole('button', { name: 'Plannen beheren' }).click();
  await page.getByRole('button', { name: 'Kopie maken' }).click();

  const poolItem = page.getByTestId(`pool-${task._id}`);
  await mouseDrag(page, poolItem, page.getByTestId(`cell:0:2:${bram._id}`));
  await expect(page.getByRole('alert')).toContainText('Bram kan niet op dinsdag. "Ramen zemen" is niet geplaatst.');
  await expect(page.getByTestId(`cell:0:2:${bram._id}`)).not.toContainText('Ramen zemen');

  await mouseDrag(page, poolItem, page.getByTestId(`cell:0:2:${anna._id}`));
  await expect(page.getByTestId(`cell:0:2:${anna._id}`)).toContainText('Ramen zemen');
  await expect(poolItem).toBeHidden();
  await expect(page.getByText('Opgeslagen', { exact: true })).toBeVisible();

  await page.getByRole('button', { name: 'Plannen beheren' }).click();
  await page.getByRole('button', { name: 'Dit plan activeren' }).click();
  await page.getByRole('dialog', { name: 'Plan activeren?' }).getByRole('button', { name: 'Activeren' }).click();
  await expect(page.getByText('Plan geactiveerd.')).toBeVisible();

  const active = (await app.api<ApiPlan[]>('GET', '/api/cycle-plans')).find((p) => p.active)!;
  expect(active.slots).toEqual([expect.objectContaining({ taskId: task._id, weekIndex: 0, weekday: 2, assigneeId: anna._id })]);
});
