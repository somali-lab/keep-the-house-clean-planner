import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, householdRoutes, makeUser, mockApi, page, problem, requestsTo, sequence, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { UsersSection } from './UsersSection.tsx';

const BRAM_V3 = { ...BRAM, version: 3 };
const GUEST = makeUser({ id: 'c00000000000000000000003', name: 'Logé', active: false, dailyBudgetMinutes: { weekday: 0, weekend: 30 } });

function setup(extra: Record<string, unknown> = {}) {
  storeProfile(ANNA.id);
  return mockApi({
    ...householdRoutes([ANNA, BRAM_V3, GUEST], makeSettings()),
    '/api/v2/users': page([ANNA, BRAM_V3, GUEST]),
    [`PATCH /api/v2/users/${BRAM.id}`]: { ...BRAM_V3, version: 4 },
    'POST /api/v2/users': makeUser({ id: 'd00000000000000000000004', name: 'Chris' }),
    ...extra,
  });
}

describe('UsersSection', () => {
  it('lists people with their budgets and marks inactive ones', async () => {
    setup();
    renderWithProviders(<UsersSection />);
    const section = await screen.findByRole('region', { name: 'Personen' });
    await waitFor(() => expect(within(section).getAllByRole('listitem')).toHaveLength(3));
    const rows = within(section).getAllByRole('listitem').map((li) => li.textContent);
    expect(rows[0]).toContain('AnnaBeheerder60 min doordeweeks · 120 min weekend');
    expect(rows[0]).toContain('max. 480 min per werkdag · 480 min per weekenddag');
    expect(rows[2]).toContain('LogéHuisgenoot(inactief)0 min doordeweeks · 30 min weekend');
  });

  it('edits name, availability, budget and active state, with the version of the person as If-Match', async () => {
    const fetchMock = setup();
    renderWithProviders(<UsersSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Bram de Vries' }));
    const form = screen.getByRole('form', { name: 'Bewerk Bram de Vries' });

    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: '  Bram  ' } });
    fireEvent.change(within(form).getByLabelText('Rol'), { target: { value: 'planner' } });
    fireEvent.click(within(form).getByLabelText('zaterdag'));
    fireEvent.click(within(form).getByLabelText('maandag'));
    fireEvent.change(within(form).getByLabelText('Budget weekend (zaterdag en zondag samen)'), { target: { value: '90' } });
    fireEvent.change(within(form).getByLabelText('Maximaal per werkdag (minuten)'), { target: { value: '45' } });
    fireEvent.change(within(form).getByLabelText('Maximaal per weekenddag (minuten)'), { target: { value: '75' } });
    fireEvent.click(within(form).getByLabelText('Actief'));
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() => expect(requestsTo(fetchMock, 'PATCH', `/api/v2/users/${BRAM.id}`)).toHaveLength(1));
    const [request] = requestsTo(fetchMock, 'PATCH', `/api/v2/users/${BRAM.id}`);
    expect(request!.body).toEqual({
      name: 'Bram',
      color: BRAM.color,
      role: 'planner',
      unavailableWeekdays: [1, 6],
      dailyBudgetMinutes: { weekday: 60, weekend: 90 },
      maxDailyMinutes: { weekday: 45, weekend: 75 },
      active: false,
    });
    expect(request!.headers['if-match']).toBe('"3"');
    expect(await screen.findByRole('status')).toHaveTextContent('Opgeslagen.');
    expect(screen.queryByRole('form')).not.toBeInTheDocument();
  });

  it('keeps the edit and says so when the person changed in the meantime (412)', async () => {
    setup({ [`PATCH /api/v2/users/${BRAM.id}`]: problem(412, 'precondition_failed', 'The person changed.') });
    renderWithProviders(<UsersSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Bram de Vries' }));
    const form = screen.getByRole('form', { name: 'Bewerk Bram de Vries' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Bram B' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    expect(await within(form).findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    expect(within(form).getByLabelText('Naam')).toHaveValue('Bram B');
  });

  it('says that the last administrator cannot be demoted or deactivated (409 last_admin)', async () => {
    setup({ [`PATCH /api/v2/users/${BRAM.id}`]: problem(409, 'last_admin', 'Last admin.') });
    renderWithProviders(<UsersSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Bram de Vries' }));
    const form = screen.getByRole('form', { name: 'Bewerk Bram de Vries' });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Er moet minstens één actieve beheerder blijven.');
  });

  it('sends the version that was read again when the person saves a second time after a 412', async () => {
    const url = `/api/v2/users/${BRAM.id}`;
    const fetchMock = setup({
      '/api/v2/users': sequence(page([ANNA, BRAM_V3, GUEST]), page([ANNA, { ...BRAM, version: 6 }, GUEST])),
      [`PATCH ${url}`]: sequence(() => problem(412, 'precondition_failed', 'Changed.'), { ...BRAM, version: 7 }),
    });
    renderWithProviders(<UsersSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Bram de Vries' }));
    const form = screen.getByRole('form', { name: 'Bewerk Bram de Vries' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Bram B' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await within(form).findByRole('alert');
    await waitFor(() => expect(requestsTo(fetchMock, 'GET', '/api/v2/users?limit=500').length).toBeGreaterThan(1));
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(requestsTo(fetchMock, 'PATCH', url)).toHaveLength(2));
    expect(requestsTo(fetchMock, 'PATCH', url).map((r) => r.headers['if-match'])).toEqual(['"3"', '"6"']);
  });

  it('shows the first field error of a refused value', async () => {
    setup({ [`PATCH /api/v2/users/${BRAM.id}`]: () => problem(400, 'validation_error', 'Invalid.', { errors: { color: ['invalid_color'] } }) });
    renderWithProviders(<UsersSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Bram de Vries' }));
    const form = screen.getByRole('form', { name: 'Bewerk Bram de Vries' });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Ongeldige waarde voor color: invalid_color.');
  });

  it('adds a person with default budgets', async () => {
    const fetchMock = setup();
    renderWithProviders(<UsersSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Persoon toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe persoon' });
    expect(within(form).queryByLabelText('Actief')).not.toBeInTheDocument();

    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Chris' } });
    fireEvent.change(within(form).getByLabelText('Kleur'), { target: { value: '#16a34a' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() => expect(requestsTo(fetchMock, 'POST', '/api/v2/users')).toHaveLength(1));
    expect(requestsTo(fetchMock, 'POST', '/api/v2/users')[0]!.body).toEqual({
      name: 'Chris',
      color: '#16a34a',
      role: 'member',
      unavailableWeekdays: [],
      dailyBudgetMinutes: { weekday: 60, weekend: 120 },
      maxDailyMinutes: { weekday: 60, weekend: 120 },
    });
  });

  it('explains invalid input without saving', async () => {
    const fetchMock = setup();
    renderWithProviders(<UsersSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Persoon toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe persoon' });

    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(within(form).getByRole('alert')).toHaveTextContent('Vul een naam in.');

    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Chris' } });
    expect(within(form).getByLabelText('Naam')).toHaveValue('Chris');
    fireEvent.change(within(form).getByLabelText('Budget doordeweeks (maandag t/m vrijdag samen)'), { target: { value: '-5' } });
    expect(within(form).getByLabelText('Budget doordeweeks (maandag t/m vrijdag samen)')).toHaveValue(-5);
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(within(form).getByRole('alert')).toHaveTextContent('Een budget is een heel aantal minuten, 0 of meer.');
    expect(requestsTo(fetchMock, 'POST', '/api/v2/users')).toEqual([]);
  });
});
