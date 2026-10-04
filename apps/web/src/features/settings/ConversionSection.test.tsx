import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, LIMITS, householdRoutes, mockApi, problem, requestsTo, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { ConversionSection, currencyCodes, parseCentsPerPoint } from './ConversionSection.tsx';

function setup(profileId: string, settings = makeSettings({ currencyCode: 'EUR', centsPerPoint: 10 }), patch: unknown = { ...settings, version: 2 }) {
  storeProfile(profileId);
  return mockApi({
    ...householdRoutes([ANNA, BRAM], settings),
    '/api/v2/meta/limits': LIMITS,
    'PATCH /api/v2/settings': patch,
  });
}

const patchBodies = (fetchMock: ReturnType<typeof mockApi>) =>
  requestsTo(fetchMock, 'PATCH', '/api/v2/settings').map((request) => request.body);

describe('parseCentsPerPoint', () => {
  it.each([
    ['0', 0],
    ['10', 10],
    ['10000', 10000],
    [' 25 ', 25],
    ['10001', null],
    ['-1', null],
    ['2.5', null],
    ['', null],
    ['abc', null],
  ])('%j → %s', (text, expected) => {
    expect(parseCentsPerPoint(text)).toBe(expected);
  });
});

describe('currencyCodes', () => {
  it('lists real currencies and always includes the one in force', () => {
    const codes = currencyCodes('EUR');
    expect(codes).toEqual(expect.arrayContaining(['EUR', 'USD', 'GBP']));
    expect(currencyCodes('ZZZ')).toContain('ZZZ');
  });

  it('only offers currencies with two fraction digits: EUR and USD yes, JPY and KWD no', () => {
    const codes = currencyCodes('EUR');
    expect(codes).toEqual(expect.arrayContaining(['EUR', 'USD']));
    expect(codes).not.toContain('JPY');
    expect(codes).not.toContain('KWD');
  });
});

describe('ConversionSection', () => {
  it('shows nothing to anyone but an administrator', async () => {
    setup(BRAM.id);
    const { container } = renderWithProviders(<ConversionSection settings={makeSettings()} />);
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Puntenwaarde' })).not.toBeInTheDocument());
    expect(container).toBeEmptyDOMElement();
  });

  it('shows the currency and value in force, with what one point is worth', async () => {
    setup(ANNA.id);
    renderWithProviders(<ConversionSection settings={makeSettings({ currencyCode: 'USD', centsPerPoint: 25 })} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    expect(within(form).getByLabelText('Valuta')).toHaveValue('USD');
    expect(within(form).getByLabelText('Waarde van één punt (in centen)')).toHaveValue(25);
    expect(within(form).getByText(/1 punt = US\$\s?0,25/)).toBeInTheDocument();
  });

  it('defaults to EUR and no money for settings that have no conversion yet, and says nothing is shown', async () => {
    setup(ANNA.id, makeSettings());
    renderWithProviders(<ConversionSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    expect(within(form).getByLabelText('Valuta')).toHaveValue('EUR');
    expect(within(form).getByLabelText('Waarde van één punt (in centen)')).toHaveValue(0);
    expect(within(form).getByText('Er worden geen geldbedragen getoond.')).toBeInTheDocument();
  });

  it('updates the preview while typing', async () => {
    setup(ANNA.id, makeSettings());
    renderWithProviders(<ConversionSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.change(within(form).getByLabelText('Waarde van één punt (in centen)'), { target: { value: '150' } });
    expect(within(form).getByText(/1 punt = €\s1,50/)).toBeInTheDocument();
    fireEvent.change(within(form).getByLabelText('Valuta'), { target: { value: 'GBP' } });
    expect(within(form).getByText(/1 punt = /)).toHaveTextContent(/£\s?1,50/);
  });

  it('refuses a value outside 0 to 10000 or that is not a whole number, without calling the server', async () => {
    const fetchMock = setup(ANNA.id);
    renderWithProviders(<ConversionSection settings={makeSettings({ currencyCode: 'EUR', centsPerPoint: 10 })} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    for (const bad of ['10001', '-1', '2.5', '']) {
      fireEvent.change(within(form).getByLabelText('Waarde van één punt (in centen)'), { target: { value: bad } });
      fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
      expect(within(form).getByRole('alert')).toHaveTextContent('De waarde van een punt moet een heel getal van 0 tot 10000 zijn.');
    }
    expect(patchBodies(fetchMock)).toEqual([]);
  });

  it('saves the currency and the cents per point together and confirms it', async () => {
    const fetchMock = setup(ANNA.id);
    renderWithProviders(<ConversionSection settings={makeSettings({ currencyCode: 'EUR', centsPerPoint: 10 })} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.change(within(form).getByLabelText('Valuta'), { target: { value: 'GBP' } });
    fireEvent.change(within(form).getByLabelText('Waarde van één punt (in centen)'), { target: { value: '20' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    await waitFor(() => expect(patchBodies(fetchMock)).toEqual([{ currencyCode: 'GBP', centsPerPoint: 20 }]));
    expect(await within(form).findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('does not offer JPY or KWD, and refuses one that is stored anyway, without calling the server', async () => {
    const fetchMock = setup(ANNA.id);
    renderWithProviders(<ConversionSection settings={makeSettings({ currencyCode: 'JPY', centsPerPoint: 10 })} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    const options = within(within(form).getByLabelText('Valuta')).getAllByRole('option').map((option) => (option as HTMLOptionElement).value);
    expect(options).not.toContain('KWD');
    expect(options.filter((code) => code === 'JPY')).toHaveLength(1); // only the stored one, so the select can show it
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    expect(within(form).getByRole('alert')).toHaveTextContent('Deze valuta heeft geen twee decimalen');
    expect(patchBodies(fetchMock)).toEqual([]);
    fireEvent.change(within(form).getByLabelText('Valuta'), { target: { value: 'EUR' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    await waitFor(() => expect(patchBodies(fetchMock)).toEqual([{ currencyCode: 'EUR', centsPerPoint: 10 }]));
  });

  it('sends the version of the settings as If-Match', async () => {
    const settings = makeSettings({ currencyCode: 'EUR', centsPerPoint: 10, version: 3 });
    const fetchMock = setup(ANNA.id, settings);
    renderWithProviders(<ConversionSection settings={settings} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    await waitFor(() => expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')).toHaveLength(1));
    expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')[0]!.headers['if-match']).toBe('"3"');
  });

  it('keeps the typed value and says so when the settings changed in the meantime (412)', async () => {
    setup(ANNA.id, makeSettings({ currencyCode: 'EUR', centsPerPoint: 10 }), problem(412, 'precondition_failed', 'Changed.'));
    renderWithProviders(<ConversionSection settings={makeSettings({ currencyCode: 'EUR', centsPerPoint: 10 })} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.change(within(form).getByLabelText('Waarde van één punt (in centen)'), { target: { value: '40' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    expect(within(form).getByLabelText('Waarde van één punt (in centen)')).toHaveValue(40);
  });

  it('turns money off with 0', async () => {
    const fetchMock = setup(ANNA.id);
    renderWithProviders(<ConversionSection settings={makeSettings({ currencyCode: 'EUR', centsPerPoint: 10 })} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.change(within(form).getByLabelText('Waarde van één punt (in centen)'), { target: { value: '0' } });
    expect(within(form).getByText('Er worden geen geldbedragen getoond.')).toBeInTheDocument();
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    await waitFor(() => expect(patchBodies(fetchMock)).toEqual([{ currencyCode: 'EUR', centsPerPoint: 0 }]));
  });

  it('shows an error when the server refuses', async () => {
    setup(ANNA.id, makeSettings(), problem(403, 'permission_denied', 'Not allowed.'));
    renderWithProviders(<ConversionSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Er ging iets mis.');
  });
});
