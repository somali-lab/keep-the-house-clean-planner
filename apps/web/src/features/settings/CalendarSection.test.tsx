import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, householdRoutes, mockApi, problem, requestsTo, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { CalendarSection, isMondayKey } from './CalendarSection.tsx';

const SETTINGS = makeSettings({ vacationRanges: [{ from: '2026-12-21', to: '2027-01-03' }], version: 4 });

function setup(patch: unknown = { ...SETTINGS, version: 5 }) {
  storeProfile(ANNA.id);
  return mockApi({
    ...householdRoutes([ANNA, BRAM], SETTINGS),
    'PATCH /api/v2/settings': patch,
  });
}

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
  it('only saves a Monday, with the version of the settings', async () => {
    const fetchMock = setup();
    renderWithProviders(<CalendarSection settings={SETTINGS} />);
    const form = screen.getByRole('form', { name: 'Cyclusstart' });
    const input = within(form).getByLabelText('Startdatum (maandag)');
    expect(input).toHaveValue('2026-09-14');

    fireEvent.change(input, { target: { value: '2026-10-13' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(within(form).getByRole('alert')).toHaveTextContent('Kies een maandag.');
    expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')).toEqual([]);

    fireEvent.change(input, { target: { value: '2026-10-12' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')).toHaveLength(1));
    const [request] = requestsTo(fetchMock, 'PATCH', '/api/v2/settings');
    expect(request!.body).toEqual({ cycleAnchorDate: '2026-10-12' });
    expect(request!.headers['if-match']).toBe('"4"');
    expect(await within(form).findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('keeps the typed date and says so when the settings changed in the meantime (412)', async () => {
    setup(problem(412, 'precondition_failed', 'The settings changed.'));
    renderWithProviders(<CalendarSection settings={SETTINGS} />);
    const form = screen.getByRole('form', { name: 'Cyclusstart' });
    fireEvent.change(within(form).getByLabelText('Startdatum (maandag)'), { target: { value: '2026-10-12' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    expect(within(form).getByLabelText('Startdatum (maandag)')).toHaveValue('2026-10-12');
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
    await waitFor(() => expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')).toHaveLength(1));
    const [request] = requestsTo(fetchMock, 'PATCH', '/api/v2/settings');
    expect(request!.body).toEqual({ vacationRanges: [{ from: '2026-10-19', to: '2026-10-25' }] });
    expect(request!.headers['if-match']).toBe('"4"');
    expect(await within(form).findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('keeps the edited list and says so when the settings changed in the meantime (412)', async () => {
    setup(problem(412, 'precondition_failed', 'The settings changed.'));
    renderWithProviders(<CalendarSection settings={SETTINGS} />);
    const form = screen.getByRole('form', { name: 'Vakanties' });
    fireEvent.click(within(form).getByRole('button', { name: 'Verwijder vakantie 21-12-2026 t/m 03-01-2027' }));
    fireEvent.click(within(form).getByRole('button', { name: 'Vakanties opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    expect(within(form).queryAllByRole('listitem')).toHaveLength(0);
  });
});
