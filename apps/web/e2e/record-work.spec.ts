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
  const dialog = page.getByRole('dialog', { name: 'Extra taak' });
  const record = async (fill: () => Promise<void>, click: 'click' | 'dblclick' = 'click') => {
    await page.getByRole('button', { name: 'Extra taak' }).click();
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

test('plan an extra execution and a one-off task for a later day, one of them from the Taken screen', async ({ page, app }) => {
  const anna = await app.user('Anna');
  const task = await createTask(app, anna, { name: 'Stofzuigen', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 20 });
  await generateCycles(app, anna);
  const records = (date: string) => app.api<ApiRecord[]>('GET', `/api/occurrences?from=${date}&to=${date}`);

  await openAs(page, app, anna, '/today');
  const dialog = page.getByRole('dialog', { name: 'Extra taak' });

  // Extra execution of an existing task, planned for tomorrow for Anna: an open occurrence, without undo.
  await page.getByRole('button', { name: 'Extra taak' }).click();
  await expect(dialog.getByRole('radio', { name: 'Al gedaan (vandaag)' })).toBeChecked();
  await dialog.getByRole('radio', { name: 'Inplannen' }).check();
  await dialog.getByLabel('Taak', { exact: true }).selectOption({ label: 'Stofzuigen · Woonkamer' });
  await dialog.getByLabel('Datum').fill('2026-09-17');
  await dialog.getByLabel('Voor wie').selectOption({ label: 'Anna' });
  await dialog.getByRole('button', { name: 'Inplannen' }).click();
  await expect(dialog).toBeHidden();
  const snackbar = page.locator('.snackbar');
  await expect(snackbar).toContainText('"Stofzuigen" is ingepland op do 17-09.');
  await expect(snackbar.getByRole('button', { name: 'Ongedaan maken' })).toHaveCount(0);
  await expect.poll(async () => (await records('2026-09-17')).filter((o) => o.taskId === task._id && o.origin === 'adhoc').length).toBe(1);
  expect((await records('2026-09-17')).find((o) => o.taskId === task._id && o.origin === 'adhoc')).toMatchObject({ status: 'open' });

  // It is open work on that day.
  await page.getByRole('button', { name: 'Morgen', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Mijn taken' })).toContainText('Stofzuigen');

  // One-off task for anyone, planned from the Taken screen: it shows up there in the dated block after saving.
  await page.goto(`${app.baseURL}/tasks`);
  await expect(page.getByRole('heading', { name: 'Mijn taken' })).toBeVisible();
  await page.getByRole('button', { name: 'Extra taak' }).click();
  await dialog.getByRole('radio', { name: 'Eenmalige taak (komt niet in de takenlijst)' }).check();
  await dialog.getByRole('radio', { name: 'Inplannen' }).check();
  await dialog.getByLabel('Naam van de klus').fill('Gordijnen ophangen');
  await dialog.getByLabel('Duur (minuten)').fill('40');
  await dialog.getByLabel('Datum').fill('2026-09-18');
  await expect(dialog.getByLabel('Voor wie')).toHaveValue('');
  await dialog.getByRole('button', { name: 'Inplannen' }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole('region', { name: 'Nog niet toegewezen' })).toContainText('Gordijnen ophangen');
  await expect.poll(async () => (await records('2026-09-18')).filter((o) => o.taskId === null).length).toBe(1);
  expect((await records('2026-09-18')).find((o) => o.taskId === null)).toMatchObject({ status: 'open', taskNameSnapshot: 'Gordijnen ophangen' });
  expect(await app.api<unknown[]>('GET', '/api/tasks')).toHaveLength(1);

  // The day after tomorrow shows it as open work that nobody has picked up yet.
  await page.goto(`${app.baseURL}/today`);
  await page.getByRole('button', { name: 'Overmorgen' }).click();
  await page.getByLabel('Filter op persoon').selectOption('all');
  await expect(page.getByRole('region', { name: 'Nog niet opgepakt' })).toContainText('Gordijnen ophangen');
});
