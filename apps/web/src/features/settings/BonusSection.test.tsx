import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, LIMITS, householdRoutes, mockApi, problem, requestsTo, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { BonusSection, parseBonusAmount } from './BonusSection.tsx';

const SCHEDULE = [
  { from: '2026-09-01', weekDone: 4, weekOnTime: 2, cycleDone: 10, cycleOnTime: 5, startsInFuture: false },
  { from: '2026-09-16', weekDone: 5, weekOnTime: 3, cycleDone: 20, cycleOnTime: 10, startsInFuture: false },
];
const IN_FORCE = { weekDone: 5, weekOnTime: 3, cycleDone: 20, cycleOnTime: 10 };
const withSchedule = (extra = {}) => makeSettings({ bonusSchedule: SCHEDULE, bonusesInForce: IN_FORCE, ...extra });

function setup(profileId: string, settings = withSchedule(), patch: unknown = { ...settings, version: 2 }) {
  storeProfile(profileId);
  return mockApi({
    ...householdRoutes([ANNA, BRAM], settings),
    '/api/v2/meta/limits': LIMITS,
    'PATCH /api/v2/settings': patch,
  });
}

describe('parseBonusAmount', () => {
  it.each([
    ['0', 0],
    ['5', 5],
    ['1000', 1000],
    [' 12 ', 12],
    ['1001', null],
    ['-1', null],
    ['1.5', null],
    ['', null],
    ['abc', null],
  ])('%j → %s', (text, expected) => {
    expect(parseBonusAmount(text)).toBe(expected);
  });

  it('follows the limits of the server', () => {
    expect(parseBonusAmount('50', { minPoints: 0, maxPoints: 40 })).toBeNull();
    expect(parseBonusAmount('40', { minPoints: 0, maxPoints: 40 })).toBe(40);
  });
});

describe('BonusSection', () => {
  it('shows nothing to anyone but an administrator', async () => {
    setup(BRAM.id);
    const { container } = renderWithProviders(<BonusSection settings={makeSettings()} />);
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Bonussen' })).not.toBeInTheDocument());
    expect(container).toBeEmptyDOMElement();
  });

  it('shows the amounts in force as the server worked them out, and the schedule rows', async () => {
    setup(ANNA.id);
    renderWithProviders(<BonusSection settings={withSchedule()} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    expect(within(form).getByLabelText('Week: alles gedaan')).toHaveValue(5);
    expect(within(form).getByLabelText('Week: alles op tijd')).toHaveValue(3);
    expect(within(form).getByLabelText('Cyclus: alles gedaan')).toHaveValue(20);
    expect(within(form).getByLabelText('Cyclus: alles op tijd')).toHaveValue(10);
    expect(within(form).getByText('Deze bedragen gelden sinds 16-09-2026 en voor alles daarna.')).toBeInTheDocument();
    expect(within(form).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'Vanaf 16-09-2026: week 5 gedaan en 3 op tijd, cyclus 20 gedaan en 10 op tijd',
      'Vanaf 01-09-2026: week 4 gedaan en 2 op tijd, cyclus 10 gedaan en 5 op tijd',
    ]);
  });

  it('takes the amounts in force from the server, not from the rows: a row that starts later is only marked', async () => {
    const schedule = [...SCHEDULE, { from: '2026-09-20', weekDone: 50, weekOnTime: 30, cycleDone: 200, cycleOnTime: 100, startsInFuture: true }];
    const settings = makeSettings({ bonusSchedule: schedule, bonusesInForce: IN_FORCE });
    setup(ANNA.id, settings);
    renderWithProviders(<BonusSection settings={settings} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    expect(within(form).getByLabelText('Week: alles gedaan')).toHaveValue(5);
    const rows = within(form).getAllByRole('listitem');
    expect(rows[0]).toHaveTextContent('Vanaf 20-09-2026');
    expect(rows[0]).toHaveTextContent('nog niet van kracht');
    expect(rows[1]).not.toHaveTextContent('nog niet van kracht');
    expect(within(form).getByText('Deze bedragen gelden sinds 16-09-2026 en voor alles daarna.')).toBeInTheDocument();
  });

  it('says that bonuses are off without a schedule, with every amount at 0', async () => {
    setup(ANNA.id, makeSettings());
    renderWithProviders(<BonusSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    expect(within(form).getByText('Er zijn nog geen bonussen ingesteld; ze staan uit.')).toBeInTheDocument();
    for (const label of ['Week: alles gedaan', 'Week: alles op tijd', 'Cyclus: alles gedaan', 'Cyclus: alles op tijd']) {
      expect(within(form).getByLabelText(label)).toHaveValue(0);
    }
    expect(within(form).queryByRole('listitem')).not.toBeInTheDocument();
  });

  it('refuses amounts outside 0 to 1000 and amounts that are not whole numbers, without calling the server', async () => {
    const fetchMock = setup(ANNA.id);
    renderWithProviders(<BonusSection settings={withSchedule()} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    for (const bad of ['1001', '-1', '2.5', '']) {
      fireEvent.change(within(form).getByLabelText('Week: alles gedaan'), { target: { value: bad } });
      fireEvent.click(within(form).getByRole('button', { name: 'Bonussen opslaan' }));
      expect(within(form).getByRole('alert')).toHaveTextContent('Elk bedrag moet een heel getal van 0 tot 1000 zijn.');
    }
    expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')).toEqual([]);
  });

  it('saves the four amounts as periodBonuses with the version of the settings, and confirms it', async () => {
    const settings = withSchedule({ version: 7 });
    const fetchMock = setup(ANNA.id, settings, { ...settings, version: 8 });
    renderWithProviders(<BonusSection settings={settings} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    fireEvent.change(within(form).getByLabelText('Week: alles gedaan'), { target: { value: '7' } });
    fireEvent.change(within(form).getByLabelText('Cyclus: alles op tijd'), { target: { value: '0' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Bonussen opslaan' }));
    await waitFor(() => expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')).toHaveLength(1));
    const [request] = requestsTo(fetchMock, 'PATCH', '/api/v2/settings');
    expect(request!.body).toEqual({ periodBonuses: { weekDone: 7, weekOnTime: 3, cycleDone: 20, cycleOnTime: 0 } });
    expect(request!.headers['if-match']).toBe('"7"');
    expect(await within(form).findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('keeps the typed amounts and says so when the settings changed in the meantime (412)', async () => {
    setup(ANNA.id, withSchedule(), problem(412, 'precondition_failed', 'The settings changed.'));
    renderWithProviders(<BonusSection settings={withSchedule()} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    fireEvent.change(within(form).getByLabelText('Week: alles gedaan'), { target: { value: '9' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Bonussen opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    expect(within(form).getByLabelText('Week: alles gedaan')).toHaveValue(9);
  });

  it('says so when someone else changed the amounts in the meantime (409 bonus_schedule_conflict)', async () => {
    setup(ANNA.id, withSchedule(), problem(409, 'bonus_schedule_conflict', 'Conflict.'));
    renderWithProviders(<BonusSection settings={withSchedule()} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    fireEvent.click(within(form).getByRole('button', { name: 'Bonussen opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Iemand anders heeft de bonusbedragen net gewijzigd');
  });
});
