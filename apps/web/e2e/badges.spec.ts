import { createTask, expect, generateCycles, openAs, planOn, test, TODAY } from './fixtures.ts';

/** A real 1x1 PNG, so the browser can actually decode what the server serves. */
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');

test('an administrator adds the example badges and sets the toilet badge to two executions; a member completes the task twice and earns it', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const bram = await app.user('Bram');
  const toilet = await createTask(app, anna, { name: 'Toilet schoonmaken', room: 'Toilet', intervalKey: '1w', durationMinutes: 10 });
  await generateCycles(app, anna);

  // The administrator adds the example badges from the Badges page; the toilet example finds its task by name.
  await openAs(page, app, anna, '/manage/badges');
  await expect(page.getByRole('heading', { level: 1, name: 'Badges' })).toBeVisible();
  await page.getByRole('button', { name: 'Voorbeeldbadges toevoegen' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Voorbeeldbadges toegevoegd: 3.' })).toBeVisible();
  const rows = page.getByRole('list', { name: 'Overzicht van badges' }).getByRole('listitem');
  await expect(rows).toHaveCount(3);
  await expect(rows.filter({ hasText: 'Toiletjuffrouw' })).toContainText('10 uitvoeringen van Toilet schoonmaken');
  await expect(rows.filter({ hasText: 'Dweilkampioen' })).toContainText('(inactief)');

  // Adding them again changes nothing.
  await page.getByRole('button', { name: 'Voorbeeldbadges toevoegen' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'De voorbeeldbadges zijn al toegevoegd.' })).toBeVisible();
  await expect(rows).toHaveCount(3);

  // The threshold and the picture of the toilet badge are editable.
  await page.getByRole('button', { name: 'Bewerk Toiletjuffrouw' }).click();
  const form = page.getByRole('form', { name: 'Bewerk Toiletjuffrouw' });
  await expect(form.getByLabel('Toilet schoonmaken')).toBeChecked();
  await form.getByLabel('Aantal uitvoeringen', { exact: true }).fill('2');
  await form.getByLabel('Afbeelding kiezen').setInputFiles({ name: 'toilet.png', mimeType: 'image/png', buffer: PNG });
  await expect(form.getByRole('img', { name: 'Voorbeeld van de gekozen afbeelding' })).toBeVisible();
  await form.getByRole('button', { name: 'Opslaan' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Badge opgeslagen.' })).toBeVisible();
  const toiletRow = rows.filter({ hasText: 'Toiletjuffrouw' });
  await expect(toiletRow).toContainText('2 uitvoeringen van Toilet schoonmaken');
  // The uploaded picture is served by the server and decodes in the browser; its alternative text is the name of the badge.
  const picture = toiletRow.getByRole('img', { name: 'Toiletjuffrouw' });
  await expect(picture).toHaveAttribute('src', /^\/api\/badges\/[0-9a-f]{24}\/image\?v=[0-9a-f]{12}$/);
  await expect.poll(() => picture.evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth)).toBe(1);

  // Bram has the task twice today and checks both off.
  await planOn(app, anna, toilet, TODAY, bram);
  await planOn(app, anna, toilet, TODAY, bram);
  await openAs(page, app, bram, '/today');
  const mine = page.getByRole('region', { name: 'Mijn taken' });
  await expect(mine).toContainText('Toilet schoonmaken');
  await page.getByRole('button', { name: 'Afvinken: Toilet schoonmaken' }).first().click();
  await expect(page.locator('.snackbar')).toContainText('"Toilet schoonmaken" afgevinkt.');

  // After the first one the badge is in sight, with its progress as text.
  const badges = page.getByRole('region', { name: 'Mijn badges' });
  await expect(badges.getByRole('listitem').filter({ hasText: 'Toiletjuffrouw' })).toContainText('Nog niet behaald');
  await expect(badges.getByText('1/2')).toBeVisible();

  await page.getByRole('button', { name: 'Afvinken: Toilet schoonmaken' }).click();
  await expect(page.getByRole('button', { name: 'Afvinken: Toilet schoonmaken' })).toHaveCount(0);
  const earned = badges.getByRole('listitem').filter({ hasText: 'Toiletjuffrouw' });
  await expect(earned).toContainText('Behaald op 16 september 2026');
  await expect(earned).not.toContainText('Nog niet behaald');
  await expect(earned.getByRole('img', { name: 'Toiletjuffrouw' })).toBeVisible();

  // The server agrees, and the award is one entry of the audited history.
  const { awards } = await app.api<{ awards: { personId: string; awardedAt: string }[] }>('GET', `/api/badges/awards?personId=${bram._id}`);
  expect(awards).toHaveLength(1);
  expect(awards[0]!.awardedAt).toBe(app.now);
  expect((await app.api<{ awards: unknown[] }>('GET', `/api/badges/awards?personId=${anna._id}`)).awards).toEqual([]);

  // The Points tab shows the badges of the chosen person too.
  await openAs(page, app, bram, '/manage/statistics');
  await page.getByRole('tab', { name: 'Punten' }).click();
  const ofBram = page.getByRole('region', { name: 'Badges van Bram' });
  await expect(ofBram).toContainText('Toiletjuffrouw');
  await expect(ofBram).toContainText('Behaald op 16 september 2026');

  // Undoing one check-off takes the badge away again: it follows the data.
  const occurrences = await app.api<{ _id: string; taskId: string; status: string }[]>('GET', `/api/occurrences?from=${TODAY}&to=${TODAY}`);
  const done = occurrences.find((o) => o.taskId === toilet._id && o.status === 'done')!;
  await app.api('PATCH', `/api/occurrences/${done._id}`, { as: bram, body: { action: 'uncomplete' } });
  expect((await app.api<{ awards: unknown[] }>('GET', `/api/badges/awards?personId=${bram._id}`)).awards).toEqual([]);
});
