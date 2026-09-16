import { createTask, expect, generateCycles, MOBILE, occurrencesOn, openAs, planOn, test, TODAY } from './fixtures.ts';

test.use(MOBILE);

test('check off while offline; the change reaches the server once back online (T4.5)', async ({ page, context, app }) => {
  const anna = await app.user('Anna');
  const task = await createTask(app, anna, { name: 'Vaatwasser uitruimen', room: 'Keuken', intervalKey: '1w', durationMinutes: 10 });
  await generateCycles(app, anna);
  await planOn(app, anna, task, TODAY, anna);

  await openAs(page, app, anna, '/mobile/today');
  await expect(page.getByRole('region', { name: 'Mijn taken vandaag' })).toContainText('Vaatwasser uitruimen');

  await context.setOffline(true);
  await page.getByRole('button', { name: 'Afvinken: Vaatwasser uitruimen' }).click();
  const banner = page.getByText('1 wijziging is offline bewaard en wordt verstuurd zodra er weer verbinding is.');
  await expect(banner).toBeVisible();
  await expect(page.getByRole('region', { name: 'Afgerond vandaag' })).toContainText('Vaatwasser uitruimen');
  expect((await occurrencesOn(app, TODAY))[0]?.status).toBe('open');

  await context.setOffline(false);
  await expect(banner).toBeHidden();
  await expect.poll(async () => (await occurrencesOn(app, TODAY))[0]).toMatchObject({ status: 'done', completedBy: anna._id });
});
