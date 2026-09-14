import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, makeUser, mockApi, storeProfile } from '../../test/fixtures.ts';
import { renderWithProviders } from '../../test/render.tsx';
import { UsersSection } from './UsersSection.tsx';

const GUEST = makeUser({ _id: 'c00000000000000000000003', name: 'Logé', active: false, dailyBudgetMinutes: { weekday: 0, weekend: 30 } });

function setup() {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM, GUEST],
    [`PATCH /api/users/${BRAM._id}`]: (init: RequestInit) => ({ ...BRAM, ...JSON.parse(String(init.body)) }),
    'POST /api/users': (init: RequestInit) => makeUser({ _id: 'd00000000000000000000004', ...JSON.parse(String(init.body)) }),
  });
}

const bodies = (fetchMock: ReturnType<typeof mockApi>, method: string) =>
  fetchMock.mock.calls
    .filter(([, init]) => (init as RequestInit | undefined)?.method === method)
    .map(([u, init]) => [u, JSON.parse(String((init as RequestInit).body))]);

describe('UsersSection', () => {
  it('lists people with their budgets and marks inactive ones', async () => {
    setup();
    renderWithProviders(<UsersSection />);
    const section = await screen.findByRole('region', { name: 'Personen' });
    await waitFor(() => expect(within(section).getAllByRole('listitem')).toHaveLength(3));
    const rows = within(section).getAllByRole('listitem').map((li) => li.textContent);
    expect(rows[0]).toContain('Anna60 min doordeweeks · 120 min weekend');
    expect(rows[0]).toContain('max. 480 min per werkdag · 480 min per weekenddag');
    expect(rows[2]).toContain('Logé(inactief)0 min doordeweeks · 30 min weekend');
  });

  it('edits name, availability, budget and active state', async () => {
    const fetchMock = setup();
    renderWithProviders(<UsersSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Bram de Vries' }));
    const form = screen.getByRole('form', { name: 'Bewerk Bram de Vries' });

    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: '  Bram  ' } });
    fireEvent.click(within(form).getByLabelText('zaterdag'));
    fireEvent.click(within(form).getByLabelText('maandag'));
    fireEvent.change(within(form).getByLabelText('Budget weekend (zaterdag en zondag samen)'), { target: { value: '90' } });
    fireEvent.change(within(form).getByLabelText('Maximaal per werkdag (minuten)'), { target: { value: '45' } });
    fireEvent.change(within(form).getByLabelText('Maximaal per weekenddag (minuten)'), { target: { value: '75' } });
    fireEvent.click(within(form).getByLabelText('Actief'));
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() =>
      expect(bodies(fetchMock, 'PATCH')).toEqual([
        [
          `/api/users/${BRAM._id}`,
          {
            name: 'Bram',
            color: BRAM.color,
            unavailableWeekdays: [1, 6],
            dailyBudgetMinutes: { weekday: 60, weekend: 90 },
            maxDailyMinutes: { weekday: 45, weekend: 75 },
            active: false,
          },
        ],
      ]),
    );
    expect(await screen.findByRole('status')).toHaveTextContent('Opgeslagen.');
    expect(screen.queryByRole('form')).not.toBeInTheDocument();
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

    await waitFor(() =>
      expect(bodies(fetchMock, 'POST')).toEqual([
        [
          '/api/users',
          {
            name: 'Chris',
            color: '#16a34a',
            unavailableWeekdays: [],
            dailyBudgetMinutes: { weekday: 60, weekend: 120 },
            maxDailyMinutes: { weekday: 60, weekend: 120 },
          },
        ],
      ]),
    );
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
    expect(bodies(fetchMock, 'POST')).toEqual([]);
  });
});
