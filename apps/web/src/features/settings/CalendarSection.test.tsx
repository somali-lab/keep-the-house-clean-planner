import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { CalendarSection, isMondayKey } from './CalendarSection.tsx';

const SETTINGS = makeSettings({ vacationRanges: [{ from: '2026-12-21', to: '2027-01-03' }] });

function setup() {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': SETTINGS,
    'PATCH /api/settings': (init: RequestInit) => makeSettings(JSON.parse(String(init.body))),
  });
}

const patchBodies = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls
    .filter(([u, init]) => u === '/api/settings' && (init as RequestInit | undefined)?.method === 'PATCH')
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)));

describe('isMondayKey', () => {
  it.each([
    ['2026-09-14', true],
    ['2026-10-26', true],
    ['2026-09-15', false],
    ['', false],
  ])('%s → %s', (key, expected) => {
    expect(isMondayKey(key)).toBe(expected);
  });
});

describe('CalendarSection — cycle anchor', () => {
  it('only saves a Monday', async () => {
    const fetchMock = setup();
    renderWithProviders(<CalendarSection settings={SETTINGS} />);
    const form = screen.getByRole('form', { name: 'Cyclusstart' });
    const input = within(form).getByLabelText('Startdatum (maandag)');
    expect(input).toHaveValue('2026-09-14');

    fireEvent.change(input, { target: { value: '2026-10-13' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(within(form).getByRole('alert')).toHaveTextContent('Kies een maandag.');
    expect(patchBodies(fetchMock)).toEqual([]);

    fireEvent.change(input, { target: { value: '2026-10-12' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(patchBodies(fetchMock)).toEqual([{ cycleAnchorDate: '2026-10-12' }]));
    expect(await within(form).findByRole('status')).toHaveTextContent('Opgeslagen.');
  });
});

describe('CalendarSection — vacations', () => {
  it('adds, validates, removes and saves vacation ranges', async () => {
    const fetchMock = setup();
    renderWithProviders(<CalendarSection settings={SETTINGS} />);
    const form = screen.getByRole('form', { name: 'Vakanties' });
    expect(within(form).getByText('21-12-2026 t/m 03-01-2027')).toBeInTheDocument();

    const from = within(form).getByLabelText('Van');
    const to = within(form).getByLabelText('Tot en met');
    const add = within(form).getByRole('button', { name: 'Vakantie toevoegen' });

    fireEvent.change(from, { target: { value: '2026-10-19' } });
    fireEvent.click(add);
    expect(within(form).getByRole('alert')).toHaveTextContent('Vul beide datums in.');

    fireEvent.change(to, { target: { value: '2026-10-12' } });
    fireEvent.click(add);
    expect(within(form).getByRole('alert')).toHaveTextContent('De einddatum ligt vóór de begindatum.');

    fireEvent.change(to, { target: { value: '2026-10-25' } });
    fireEvent.click(add);
    expect(within(form).queryByRole('alert')).not.toBeInTheDocument();
    expect(within(form).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      '19-10-2026 t/m 25-10-2026×',
      '21-12-2026 t/m 03-01-2027×',
    ]);

    fireEvent.click(within(form).getByRole('button', { name: 'Verwijder vakantie 21-12-2026 t/m 03-01-2027' }));
    fireEvent.click(within(form).getByRole('button', { name: 'Vakanties opslaan' }));
    await waitFor(() => expect(patchBodies(fetchMock)).toEqual([{ vacationRanges: [{ from: '2026-10-19', to: '2026-10-25' }] }]));
    expect(await within(form).findByRole('status')).toHaveTextContent('Opgeslagen.');
  });
});
