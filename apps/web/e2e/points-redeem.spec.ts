import { actOn, createTask, expect, generateCycles, openAs, planOn, test, TODAY } from './fixtures.ts';
import type { ApiUser, AppServer } from './server.ts';

interface Balance {
  personId: string;
  points: number;
  earned: number;
  redeemed: number;
  money: { earned: number; redeemed: number; balance: number } | null;
}

async function balanceOf(app: AppServer, person: ApiUser): Promise<Balance | undefined> {
  const { balances } = await app.api<{ balances: Balance[] }>('GET', '/api/v2/points/balances?from=2026-09-14&to=2026-09-20');
  return balances.find((balance) => balance.personId === person.id);
}

test('someone earns points, an administrator sets 10 cents per point, then they redeem part of the balance and undo it the same day', async ({ page, app }) => {
  const anna = await app.user('Anna');
  // 10 minutes earn 10 points with the default of one point per minute.
  const schoonmaak = await createTask(app, anna, { name: 'Grote schoonmaak', room: 'Woonkamer', intervalKey: '1w', durationMinutes: 10 });
  await generateCycles(app, anna);
  const occurrence = await planOn(app, anna, schoonmaak, TODAY, anna);
  await actOn(app, anna, occurrence, 'complete');
  await expect.poll(async () => (await balanceOf(app, anna))?.points).toBe(10);

  // The administrator sets what a point is worth in the settings.
  await openAs(page, app, anna, '/manage/settings');
  const card = page.getByRole('form', { name: 'Puntenwaarde' });
  await expect(card).toBeVisible();
  await expect(card.getByLabel('Valuta')).toHaveValue('EUR');
  await card.getByLabel('Waarde van één punt (in centen)').fill('10');
  await expect(card.getByText(/1 punt = €\s0,10/)).toBeVisible();
  await card.getByRole('button', { name: 'Puntenwaarde opslaan' }).click();
  await expect(card.getByRole('status')).toHaveText('Opgeslagen.');
  expect(await app.api('GET', '/api/v2/settings')).toMatchObject({ currencyCode: 'EUR', centsPerPoint: 10 });

  // The Points tab shows the balance and what it is worth.
  await openAs(page, app, anna, '/manage/statistics');
  await page.getByRole('tab', { name: 'Punten' }).click();
  const balances = page.getByRole('table', { name: 'Punten per persoon' });
  const row = balances.getByRole('row', { name: /Anna/ });
  await expect(row.getByRole('cell').first()).toHaveText('10');
  await expect(row).toContainText(/€\s1,00/);

  // Redeem four points: the dialog previews the money and refuses more than the balance.
  await page.getByRole('button', { name: 'Inwisselen' }).click();
  const dialog = page.getByRole('dialog', { name: 'Punten inwisselen' });
  await expect(dialog.getByText(/Beschikbaar saldo: 10 punten \(€\s1,00\)/)).toBeVisible();
  await dialog.getByLabel('Aantal punten').fill('11');
  await dialog.getByRole('button', { name: 'Inwisselen' }).click();
  await expect(dialog.getByText('Dat is meer dan het beschikbare saldo (10 punten).')).toBeVisible();
  await dialog.getByLabel('Aantal punten').fill('4');
  await expect(dialog.getByText(/Dat is €\s0,40\./)).toBeVisible();
  await dialog.getByLabel('Notitie (optioneel)').fill('Pizza');
  await dialog.getByRole('button', { name: 'Inwisselen' }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole('status').filter({ hasText: /4 punten ingewisseld/ })).toContainText(/€\s0,40/);

  // Balance and money updated, the redemption is listed with its note and was stored once.
  await expect(row.getByRole('cell').first()).toHaveText('6');
  await expect(row).toContainText(/€\s0,60/);
  const entries = page.getByRole('table', { name: 'Posten van Anna' });
  await expect(entries.getByRole('row', { name: /Ingewisseld: Pizza/ })).toContainText(/-4/);
  expect(await balanceOf(app, anna)).toMatchObject({ points: 6, earned: 10, redeemed: 4, money: { earned: 100, redeemed: 40, balance: 60 } });

  // Undo on the same day: everything is back.
  await entries.getByRole('button', { name: 'Inwisseling van 4 punten ongedaan maken' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'De inwisseling is ongedaan gemaakt.' })).toBeVisible();
  await expect(row.getByRole('cell').first()).toHaveText('10');
  await expect(row).toContainText(/€\s1,00/);
  await expect(entries.getByText(/Ingewisseld/)).toHaveCount(0);
  expect(await balanceOf(app, anna)).toMatchObject({ points: 10, earned: 10, redeemed: 0 });
});
