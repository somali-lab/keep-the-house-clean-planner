import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { BonusSection, parseBonusAmount } from './BonusSection.tsx';

const NOW = new Date('2026-09-16T08:00:00.000Z');
const SCHEDULE = [
  { from: '2026-09-01', weekDone: 4, weekOnTime: 2, cycleDone: 10, cycleOnTime: 5 },
  { from: '2026-09-16', weekDone: 5, weekOnTime: 3, cycleDone: 20, cycleOnTime: 10 },
];

function setup(profileId: string, settings = makeSettings({ bonusSchedule: SCHEDULE })) {
  storeProfile(profileId);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': settings,
    'PATCH /api/settings': (init: RequestInit) => ({ ...settings, ...JSON.parse(String(init.body)) }),
  });
}

const patchBodies = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls
    .filter(([u, init]) => u === '/api/settings' && (init as RequestInit | undefined)?.method === 'PATCH')
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)));

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
});

describe('BonusSection', () => {
  it('shows nothing to anyone but an administrator', async () => {
    setup(BRAM._id);
    const { container } = renderWithProviders(<BonusSection settings={makeSettings()} now={NOW} />);
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Bonussen' })).not.toBeInTheDocument());
    expect(container).toBeEmptyDOMElement();
  });

  it('shows the amounts in force, since when they apply, and the schedule rows', async () => {
    setup(ANNA._id);
    renderWithProviders(<BonusSection settings={makeSettings({ bonusSchedule: SCHEDULE })} now={NOW} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    expect(within(form).getByLabelText('Week: alles gedaan')).toHaveValue(5);
    expect(within(form).getByLabelText('Week: alles op tijd')).toHaveValue(3);
    expect(within(form).getByLabelText('Cyclus: alles gedaan')).toHaveValue(20);
    expect(within(form).getByLabelText('Cyclus: alles op tijd')).toHaveValue(10);
    expect(within(form).getByText('Deze bedragen gelden voor de week van 14-09-2026 en de cyclus van 14-09-2026 en alles daarna.')).toBeInTheDocument();
    expect(within(form).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'Geldt voor de week van 14-09-2026 en de cyclus van 14-09-2026 en alles daarna: week 5 gedaan en 3 op tijd, cyclus 20 gedaan en 10 op tijd',
      'Geldt voor de week van 31-08-2026 en de cyclus van 17-08-2026 en alles daarna: week 4 gedaan en 2 op tijd, cyclus 10 gedaan en 5 op tijd',
    ]);
  });

  it('explains that a row covers the week and the cycle that contain its date, whatever the weekday', async () => {
    // A Sunday: the last day of the week that starts on 5 October, inside the cycle that starts on 28 September.
    const schedule = [{ from: '2026-10-11', weekDone: 1, weekOnTime: 2, cycleDone: 3, cycleOnTime: 4 }];
    const settings = makeSettings({ bonusSchedule: schedule, cycleAnchorDate: '2026-08-31' });
    setup(ANNA._id, settings);
    renderWithProviders(<BonusSection settings={settings} now={new Date('2026-10-12T08:00:00.000Z')} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    expect(within(form).getByText('Deze bedragen gelden voor de week van 05-10-2026 en de cyclus van 28-09-2026 en alles daarna.')).toBeInTheDocument();
    expect(within(form).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'Geldt voor de week van 05-10-2026 en de cyclus van 28-09-2026 en alles daarna: week 1 gedaan en 2 op tijd, cyclus 3 gedaan en 4 op tijd',
    ]);
  });

  it('marks a row that has not started yet, and takes the amounts of the row in force', async () => {
    const schedule = [...SCHEDULE, { from: '2026-09-20', weekDone: 50, weekOnTime: 30, cycleDone: 200, cycleOnTime: 100 }];
    setup(ANNA._id, makeSettings({ bonusSchedule: schedule }));
    renderWithProviders(<BonusSection settings={makeSettings({ bonusSchedule: schedule })} now={NOW} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    expect(within(form).getByLabelText('Week: alles gedaan')).toHaveValue(5);
    const rows = within(form).getAllByRole('listitem');
    expect(rows[0]).toHaveTextContent('Geldt voor de week van 14-09-2026 en de cyclus van 14-09-2026 en alles daarna');
    expect(rows[0]).toHaveTextContent('nog niet van kracht');
    expect(rows[1]).not.toHaveTextContent('nog niet van kracht');
    expect(within(form).getByText(/^Deze bedragen gelden voor de week van 14-09-2026 en de cyclus van 14-09-2026/)).toBeInTheDocument();
  });

  it('says so when someone else changed the amounts in the meantime', async () => {
    storeProfile(ANNA._id);
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        if (init?.method === 'PATCH') return new Response(JSON.stringify({ code: 'bonus_schedule_conflict' }), { status: 409 });
        const body = String(input).includes('users') ? [ANNA, BRAM] : makeSettings();
        return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }),
    );
    renderWithProviders(<BonusSection settings={makeSettings()} now={NOW} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    fireEvent.click(within(form).getByRole('button', { name: 'Bonussen opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Iemand anders heeft de bonusbedragen net gewijzigd');
  });

  it('says that bonuses are off without a schedule, with every amount at 0', async () => {
    setup(ANNA._id, makeSettings());
    renderWithProviders(<BonusSection settings={makeSettings()} now={NOW} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    expect(within(form).getByText('Er zijn nog geen bonussen ingesteld; ze staan uit.')).toBeInTheDocument();
    for (const label of ['Week: alles gedaan', 'Week: alles op tijd', 'Cyclus: alles gedaan', 'Cyclus: alles op tijd']) {
      expect(within(form).getByLabelText(label)).toHaveValue(0);
    }
    expect(within(form).queryByRole('listitem')).not.toBeInTheDocument();
  });

  it('refuses amounts outside 0 to 1000 and amounts that are not whole numbers, without calling the server', async () => {
    const fetchMock = setup(ANNA._id);
    renderWithProviders(<BonusSection settings={makeSettings({ bonusSchedule: SCHEDULE })} now={NOW} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    for (const bad of ['1001', '-1', '2.5', '']) {
      fireEvent.change(within(form).getByLabelText('Week: alles gedaan'), { target: { value: bad } });
      fireEvent.click(within(form).getByRole('button', { name: 'Bonussen opslaan' }));
      expect(within(form).getByRole('alert')).toHaveTextContent('Elk bedrag moet een heel getal van 0 tot 1000 zijn.');
    }
    expect(patchBodies(fetchMock)).toEqual([]);
  });

  it('saves the four amounts as periodBonuses and confirms it', async () => {
    const fetchMock = setup(ANNA._id);
    renderWithProviders(<BonusSection settings={makeSettings({ bonusSchedule: SCHEDULE })} now={NOW} />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    fireEvent.change(within(form).getByLabelText('Week: alles gedaan'), { target: { value: '7' } });
    fireEvent.change(within(form).getByLabelText('Cyclus: alles op tijd'), { target: { value: '0' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Bonussen opslaan' }));
    await waitFor(() =>
      expect(patchBodies(fetchMock)).toEqual([{ periodBonuses: { weekDone: 7, weekOnTime: 3, cycleDone: 20, cycleOnTime: 0 } }]),
    );
    expect(await within(form).findByRole('status')).toHaveTextContent('Opgeslagen.');
  });
});
