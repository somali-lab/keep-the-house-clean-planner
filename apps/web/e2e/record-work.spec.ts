import { createTask, expect, generateCycles, MOBILE, openAs, test, TODAY } from './fixtures.ts';

test.use(MOBILE);

interface ApiRecord {
  _id: string;
  taskId: string | null;
  taskNameSnapshot: string;
  roomNameSnapshot?: string | null;
  origin: 'generated' | 'adhoc';
  recordedDone?: boolean;
  status: string;
  completedBy: string | null;
}

interface ApiPlan {
  _id: string;
  active: boolean;
}

interface ApiPreview {
  previewToken: string;
  preserved: { adhoc: { occurrenceId: string; taskId: string | null; taskName: string }[] };
}

test('record two extra executions and a one-off task, undo one, and keep both kinds across a plan switch', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const task = await createTask(app, anna, { name: 'Stofzuigen', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 20 });
  await generateCycles(app, anna);
  const recordsToday = () => app.api<ApiRecord[]>('GET', `/api/occurrences?from=${TODAY}&to=${TODAY}`);
  const extrasOf = async () => (await recordsToday()).filter((o) => o.taskId === task._id && o.origin === 'adhoc');
  const oneOffs = async () => (await recordsToday()).filter((o) => o.taskId === null);

  await openAs(page, app, anna, '/today');
  const finished = page.getByRole('region', { name: 'Afgerond' });
  const dialog = page.getByRole('dialog', { name: 'Gedaan werk vastleggen' });
  const record = async (fill: () => Promise<void>, click: 'click' | 'dblclick' = 'click') => {
    await page.getByRole('button', { name: 'Werk vastleggen' }).click();
    await expect(dialog.getByRole('radio', { name: 'Extra keer voor een bestaande taak' })).toBeChecked();
    await fill();
    await dialog.getByRole('button', { name: 'Vastleggen' })[click]();
    await expect(dialog).toBeHidden();
  };
  const chooseTask = async () => {
    await dialog.getByLabel('Taak', { exact: true }).selectOption({ label: 'Stofzuigen · Woonkamer' });
  };

  // A double click on the submit button creates exactly one record.
  await record(chooseTask, 'dblclick');
  await expect(finished).toContainText('Stofzuigen');
  await expect.poll(async () => (await extrasOf()).length).toBe(1);
  expect((await extrasOf())[0]).toMatchObject({ recordedDone: true, status: 'done', completedBy: anna._id });

  // A deliberate second execution of the same task on the same day is a second record.
  await record(chooseTask);
  await expect.poll(async () => (await extrasOf()).length).toBe(2);
  await expect(finished.getByText('Extra')).toHaveCount(2);

  // The one-off task never gets a task record.
  const taskCount = (await app.api<unknown[]>('GET', '/api/tasks')).length;
  await record(async () => {
    await dialog.getByRole('radio', { name: 'Eenmalige taak (komt niet in de takenlijst)' }).check();
    await dialog.getByLabel('Naam van de klus').fill('Gordijnen ophangen');
    await dialog.getByLabel('Ruimte (optioneel)').selectOption({ label: 'Woonkamer' });
    await dialog.getByLabel('Duur (minuten)').fill('40');
  });
  await expect(finished).toContainText('Gordijnen ophangen');
  await expect.poll(async () => (await oneOffs()).length).toBe(1);
  expect((await oneOffs())[0]).toMatchObject({ taskNameSnapshot: 'Gordijnen ophangen', roomNameSnapshot: 'Woonkamer', recordedDone: true, status: 'done' });
  expect(await app.api<unknown[]>('GET', '/api/tasks')).toHaveLength(taskCount);

  // Undo one extra execution: the record is deleted, not reopened.
  await finished.getByRole('button', { name: 'Stofzuigen ongedaan maken' }).first().click();
  // Extra badges: the remaining extra execution and the one-off task, both recorded outside the plan.
  await expect(finished.getByText('Extra')).toHaveCount(2);
  await expect.poll(async () => (await extrasOf()).length).toBe(1);

  // Reload: both kinds are still there.
  await page.reload();
  await expect(finished).toContainText('Stofzuigen');
  await expect(finished).toContainText('Gordijnen ophangen');
  expect(await extrasOf()).toHaveLength(1);
  expect(await oneOffs()).toHaveLength(1);

  // Activate another plan through its preview: ad-hoc work is listed as preserved and survives.
  const [source] = (await app.api<ApiPlan[]>('GET', '/api/cycle-plans')).filter((p) => p.active);
  const copy = await app.api<ApiPlan>('POST', '/api/cycle-plans', { as: anna, body: { name: 'Tweede plan', copyFromId: source!._id } });
  await app.api('PUT', `/api/cycle-plans/${copy._id}/slots`, {
    as: anna,
    body: { slots: [{ taskId: task._id, weekIndex: 0, weekday: 3, assigneeId: anna._id }] },
  });
  const preview = await app.api<ApiPreview>('GET', `/api/cycle-plans/${copy._id}/activation-preview`, { as: anna });
  expect(preview.preserved.adhoc.map((item) => item.taskName).sort()).toEqual(['Gordijnen ophangen', 'Stofzuigen']);
  expect(preview.preserved.adhoc.some((item) => item.taskId === null)).toBe(true);
  await app.api('POST', `/api/cycle-plans/${copy._id}/activate`, { as: anna, body: { previewToken: preview.previewToken } });

  const after = await recordsToday();
  expect(after.filter((o) => o.origin === 'adhoc' && o.recordedDone).map((o) => o.taskNameSnapshot).sort()).toEqual(['Gordijnen ophangen', 'Stofzuigen']);
  // The slot of the new plan is generated next to the extra execution on the same day.
  expect(after.filter((o) => o.taskId === task._id && o.origin === 'generated')).toHaveLength(1);

  await page.reload();
  await expect(finished).toContainText('Gordijnen ophangen');
  await expect(finished.getByText('Extra')).toHaveCount(2);

  // Statistics count the extra execution under its task and room, and the one-off task under its room.
  await page.getByRole('button', { name: 'Instellingen en beheer openen' }).click();
  await page.getByRole('navigation', { name: 'Hoofdmenu' }).getByRole('link', { name: 'Statistiek' }).click();
  await page.getByRole('tab', { name: 'Voltooiing' }).click();
  const table = page.getByRole('table').filter({ hasText: 'Blijven liggen' });
  await expect(table.getByRole('row', { name: /Stofzuigen/ }).getByRole('cell').first()).toHaveText('1');
  await expect(table.getByRole('row', { name: /Eenmalige taak/ }).getByRole('cell').first()).toHaveText('1');

  await page.getByLabel('Voltooiing per').selectOption('room');
  await expect(table.getByRole('row', { name: /Woonkamer/ }).getByRole('cell').first()).toHaveText('2');
});
