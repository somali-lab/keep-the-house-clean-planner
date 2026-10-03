import { expect, MOBILE, openAs, test } from './fixtures.ts';

for (const [label, options] of [
  ['mobile', MOBILE],
  ['desktop', { viewport: { width: 1280, height: 900 } }],
] as const) {
  test.describe(label, () => {
    test.use(options);

    test('opens the About page from the management menu', async ({ page, app }) => {
      const anna = await app.user('Anna');

      await openAs(page, app, anna, '/');
      await page.getByRole('button', { name: 'Instellingen en beheer openen' }).click();
      await page.getByRole('navigation', { name: 'Hoofdmenu' }).getByRole('link', { name: 'Over', exact: true }).click();

      await expect(page).toHaveURL(/\/manage\/about$/);
      await expect(page.getByRole('heading', { level: 1, name: 'Over Keep the House Clean' })).toBeVisible();
      // The E2E build is a local build, so it shows no release moment of its own.
      await expect(page.getByText('Niet beschikbaar: dit is een lokale build, geen uitgebrachte release.')).toBeVisible();
      await expect(page.getByRole('link', { name: /^MIT-licentie bekijken/ })).toHaveAttribute('href', /\/blob\/main\/LICENSE$/);
      await expect(page.getByRole('link', { name: /^Changelog bekijken/ })).toHaveAttribute('href', /\/blob\/main\/CHANGELOG\.md$/);
    });
  });
}
