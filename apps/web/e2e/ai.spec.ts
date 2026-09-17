import { createTask, expect, openAs, test } from './fixtures.ts';

test('AI with the mock provider: propose, review the changes, apply, and see it in the history', async ({ page, app }) => {
  const anna = await app.user('Anna');
  await app.api('PATCH', '/api/settings', { as: anna, body: { aiProvider: { type: 'mock' } } });
  await createTask(app, anna, { name: 'Afwassen', room: 'Keuken', intervalKey: '1w', durationMinutes: 15 });
  await createTask(app, anna, { name: 'Stofzuigen', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 20 });

  await openAs(page, app, anna, '/ai');
  await page.getByRole('button', { name: 'Voorstel maken' }).click();
  await expect(page.getByText('Er staat een nieuw voorstel klaar.')).toBeVisible();

  const review = page.getByRole('region', { name: 'Voorstel bekijken' });
  // Both tasks four times, against an empty active plan.
  await expect(review).toContainText('8 toegevoegd, 0 verwijderd, 0 verplaatst, 0 ongewijzigd');
  await expect(review).toContainText('Toegevoegd: Afwassen');

  await review.getByRole('button', { name: 'Toepassen' }).click();
  await expect(page.getByText('Voorstel toegepast. Het plan is actief.')).toBeVisible();

  await page.getByRole('navigation', { name: 'Hoofdmenu' }).getByRole('link', { name: 'Geschiedenis' }).click();
  await expect(page.locator('.history-list').getByText('via AI-voorstel').first()).toBeVisible();
});
