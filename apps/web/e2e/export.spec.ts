import { readFile } from 'node:fs/promises';
import { PDFParse } from 'pdf-parse';
import { expect, generateCycles, openAs, test } from './fixtures.ts';

test('export two weeks as PDF: the file name names both weeks and there are two pages', async ({ page, app }) => {
  const anna = await app.user('Anna');
  await generateCycles(app, anna);

  await openAs(page, app, anna, '/planner');
  await page.getByRole('button', { name: 'Plannen beheren' }).click();
  await page.getByRole('button', { name: 'PDF exporteren' }).click();
  const dialog = page.getByRole('dialog', { name: 'PDF exporteren' });
  await dialog.getByLabel('2 weken').check();
  await expect(dialog.getByLabel('Startweek')).toHaveValue('2026-W38');

  const [download] = await Promise.all([
    page.waitForEvent('download'),
    dialog.getByRole('link', { name: 'Download PDF' }).click(),
  ]);
  expect(download.suggestedFilename()).toBe('huishoudschema-2026-w38-w39.pdf');

  const parser = new PDFParse({ data: await readFile((await download.path())!) });
  try {
    const result = await parser.getText();
    expect(result.total).toBe(2);
  } finally {
    await parser.destroy();
  }
});
