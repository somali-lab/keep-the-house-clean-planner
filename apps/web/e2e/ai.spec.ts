import { createTask, expect, openAs, test } from './fixtures.ts';

test('AI with the mock provider: create a plan and activate it through plan management', async ({ page, app }) => {
  const anna = await app.user('Anna');
  await app.api('PATCH', '/api/settings', { as: anna, body: { aiProvider: { type: 'mock' } } });
  await createTask(app, anna, { name: 'Afwassen', room: 'Keuken', intervalKey: '1w', durationMinutes: 15 });
  await createTask(app, anna, { name: 'Stofzuigen', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 20 });

  await openAs(page, app, anna, '/manage/planner');
  await page.getByRole('button', { name: 'Plannen beheren' }).click();
  await page.getByRole('button', { name: 'Voorstel maken' }).click();
  await expect(page.getByText('Plan aangemaakt. Bekijk, activeer of verwijder het via Plannen beheren.')).toBeVisible();

  const planSelect = page.getByLabel('Plan', { exact: true });
  const aiPlanId = await planSelect.locator('option').filter({ hasText: 'AI-voorstel' }).getAttribute('value');
  await planSelect.selectOption(aiPlanId!);
  await page.getByRole('button', { name: 'Dit plan activeren' }).click();
  await page.getByRole('dialog', { name: 'Plan activeren?' }).getByRole('button', { name: 'Activeren' }).click();
  await expect(page.getByText('Plan geactiveerd.')).toBeVisible();

  await page.getByRole('navigation', { name: 'Hoofdmenu' }).getByRole('link', { name: 'Geschiedenis' }).click();
  await expect(page.locator('.history-list').getByText('via AI-voorstel').first()).toBeVisible();
});
