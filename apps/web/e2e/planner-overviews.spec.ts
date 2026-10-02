import { createTask, expect, generateCycles, mouseDrag, openAs, test } from './fixtures.ts';

test('a task added to the active planner appears in week overview and mobile tasks after reload', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const task = await createTask(app, anna, {
    name: 'Ramen zemen',
    room: 'Woonkamer',
    intervalKey: '4wk',
    durationMinutes: 20,
  });
  await generateCycles(app, anna);

  await openAs(page, app, anna, '/manage/planner');
  await mouseDrag(page, page.getByTestId(`pool-${task._id}`), page.getByTestId(`cell:0:4:${anna._id}`));
  await expect(page.getByText('Opgeslagen', { exact: true })).toBeVisible();

  await page.goto(`${app.baseURL}/`);
  await expect(page.getByTestId('day:2026-09-17')).toContainText('Ramen zemen');

  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto(`${app.baseURL}/tasks`);
  await expect(page.getByRole('heading', { name: 'Mijn taken' })).toBeVisible();
  await expect(page.getByRole('table')).toContainText('Ramen zemen');
  await page.reload();
  await expect(page.getByRole('table')).toContainText('Ramen zemen');
});
