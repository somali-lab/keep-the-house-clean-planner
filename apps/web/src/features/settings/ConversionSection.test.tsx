import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { ConversionSection, currencyCodes, parseCentsPerPoint } from './ConversionSection.tsx';

function setup(profileId: string, settings = makeSettings({ currencyCode: 'EUR', centsPerPoint: 10 })) {
  storeProfile(profileId);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': settings,
    'PATCH /api/settings': (init: RequestInit | undefined) => ({ ...settings, ...JSON.parse(String(init?.body)) }),
  });
}

const patchBodies = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls
    .filter(([u, init]) => u === '/api/settings' && (init as RequestInit | undefined)?.method === 'PATCH')
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)));

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
});

describe('ConversionSection', () => {
  it('shows nothing to anyone but an administrator', async () => {
    setup(BRAM._id);
    const { container } = renderWithProviders(<ConversionSection settings={makeSettings()} />);
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Puntenwaarde' })).not.toBeInTheDocument());
    expect(container).toBeEmptyDOMElement();
  });

  it('shows the currency and value in force, with what one point is worth', async () => {
    setup(ANNA._id);
    renderWithProviders(<ConversionSection settings={makeSettings({ currencyCode: 'USD', centsPerPoint: 25 })} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    expect(within(form).getByLabelText('Valuta')).toHaveValue('USD');
    expect(within(form).getByLabelText('Waarde van één punt (in centen)')).toHaveValue(25);
    expect(within(form).getByText(/1 punt = US\$\s?0,25/)).toBeInTheDocument();
  });

  it('defaults to EUR and no money for settings that have no conversion yet, and says nothing is shown', async () => {
    setup(ANNA._id, makeSettings());
    renderWithProviders(<ConversionSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    expect(within(form).getByLabelText('Valuta')).toHaveValue('EUR');
    expect(within(form).getByLabelText('Waarde van één punt (in centen)')).toHaveValue(0);
    expect(within(form).getByText('Er worden geen geldbedragen getoond.')).toBeInTheDocument();
  });

  it('updates the preview while typing', async () => {
    setup(ANNA._id, makeSettings());
    renderWithProviders(<ConversionSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.change(within(form).getByLabelText('Waarde van één punt (in centen)'), { target: { value: '150' } });
    expect(within(form).getByText(/1 punt = €\s1,50/)).toBeInTheDocument();
    fireEvent.change(within(form).getByLabelText('Valuta'), { target: { value: 'JPY' } });
    expect(within(form).getByText(/1 punt = /)).toHaveTextContent(/¥|JP¥/);
  });

  it('refuses a value outside 0 to 10000 or that is not a whole number, without calling the server', async () => {
    const fetchMock = setup(ANNA._id);
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
    const fetchMock = setup(ANNA._id);
    renderWithProviders(<ConversionSection settings={makeSettings({ currencyCode: 'EUR', centsPerPoint: 10 })} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.change(within(form).getByLabelText('Valuta'), { target: { value: 'GBP' } });
    fireEvent.change(within(form).getByLabelText('Waarde van één punt (in centen)'), { target: { value: '20' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    await waitFor(() => expect(patchBodies(fetchMock)).toEqual([{ currencyCode: 'GBP', centsPerPoint: 20 }]));
    expect(await within(form).findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('turns money off with 0', async () => {
    const fetchMock = setup(ANNA._id);
    renderWithProviders(<ConversionSection settings={makeSettings({ currencyCode: 'EUR', centsPerPoint: 10 })} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.change(within(form).getByLabelText('Waarde van één punt (in centen)'), { target: { value: '0' } });
    expect(within(form).getByText('Er worden geen geldbedragen getoond.')).toBeInTheDocument();
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    await waitFor(() => expect(patchBodies(fetchMock)).toEqual([{ currencyCode: 'EUR', centsPerPoint: 0 }]));
  });

  it('shows an error when the server refuses', async () => {
    storeProfile(ANNA._id);
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        if (init?.method === 'PATCH') return new Response(JSON.stringify({ code: 'permission_denied' }), { status: 403 });
        const body = String(input).includes('users') ? [ANNA, BRAM] : makeSettings();
        return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }),
    );
    renderWithProviders(<ConversionSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Er ging iets mis.');
  });
});
