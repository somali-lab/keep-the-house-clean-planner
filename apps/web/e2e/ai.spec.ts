import { createTask, expect, openAs, test } from './fixtures.ts';

test('AI with the mock provider: drafts open in plan management, one is deleted and another is activated through the preview', async ({ page, app }) => {
  const anna = await app.user('Anna');
  await app.api('PATCH', '/api/settings', { as: anna, body: { aiProvider: { type: 'mock' } } });
  await createTask(app, anna, { name: 'Afwassen', room: 'Keuken', intervalKey: '1w', durationMinutes: 15 });
  await createTask(app, anna, { name: 'Stofzuigen', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 20 });

  const aiCard = page.getByRole('region', { name: 'AI-concept' });
  const proposeDraft = async () => {
    await page.getByRole('button', { name: 'Plannen beheren' }).click();
    await page.getByRole('button', { name: 'Voorstel maken' }).click();
  };

  await openAs(page, app, anna, '/manage/planner');

  // First draft: it opens by itself, with the AI card, without picking it from the dropdown.
  await proposeDraft();
  await expect(page.getByText('AI-concept aangemaakt en hieronder geopend. Je actieve plan is niet gewijzigd.')).toBeVisible();
  await expect(aiCard).toBeVisible();
  await expect(aiCard).toContainText('Je actieve plan verandert pas als je dit concept activeert');
  await expect(aiCard.getByRole('listitem')).toHaveCount(4);
  await expect(aiCard).toBeFocused();

  // Delete it again through the normal plan management, opened from the AI card.
  await aiCard.getByRole('button', { name: 'Plannen beheren' }).click();
  await page.getByRole('button', { name: 'Plan verwijderen' }).click();
  await page.getByRole('dialog', { name: 'Plan verwijderen?' }).getByRole('button', { name: 'Definitief verwijderen' }).click();
  await expect(page.getByText('Plan verwijderd.')).toBeVisible();
  await expect(aiCard).toBeHidden();

  // Second draft: opens by itself and is activated through the activation preview.
  await proposeDraft();
  await expect(aiCard).toBeFocused();
  await aiCard.getByRole('button', { name: 'Plannen beheren' }).click();
  await page.getByRole('button', { name: 'Dit plan activeren' }).click();
  await page.getByRole('dialog', { name: 'Plan activeren?' }).getByRole('button', { name: 'Activeren' }).click();
  await expect(page.getByText('Plan geactiveerd.')).toBeVisible();
  await expect(aiCard).toBeHidden();

  await page.getByRole('navigation', { name: 'Hoofdmenu' }).getByRole('link', { name: 'Geschiedenis' }).click();
  await expect(page.locator('.history-list').getByText('via AI-voorstel').first()).toBeVisible();
});
