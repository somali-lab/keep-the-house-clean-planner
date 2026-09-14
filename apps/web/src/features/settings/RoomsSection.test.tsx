import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeRoom, makeTask, renderWithProviders } from '../../test/render.tsx';
import { RoomsSection } from './RoomsSection.tsx';

const KEUKEN = makeRoom({ _id: 'r00000000000000000000001', name: 'Keuken', sortOrder: 20 });
const BADKAMER = makeRoom({ _id: 'r00000000000000000000002', name: 'Badkamer', sortOrder: 10 });
const SCHUUR = makeRoom({ _id: 'r00000000000000000000003', name: 'Schuur', sortOrder: 30, active: false });

function setup(extra: Record<string, unknown> = {}) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/rooms': [KEUKEN, SCHUUR, BADKAMER],
    '/api/tasks': [],
    [`PATCH /api/rooms/${KEUKEN._id}`]: (init: RequestInit) => ({ ...KEUKEN, ...JSON.parse(String(init.body)) }),
    'POST /api/rooms': (init: RequestInit) => makeRoom({ _id: 'r00000000000000000000004', ...JSON.parse(String(init.body)) }),
    ...extra,
  });
}

const bodies = (fetchMock: ReturnType<typeof mockApi>, method: string) =>
  fetchMock.mock.calls
    .filter(([, init]) => (init as RequestInit | undefined)?.method === method)
    .map(([u, init]) => [u, JSON.parse(String((init as RequestInit).body))]);

describe('RoomsSection', () => {
  it('lists rooms in their order and marks inactive ones', async () => {
    setup();
    renderWithProviders(<RoomsSection />);
    const section = await screen.findByRole('region', { name: 'Ruimtes' });
    await waitFor(() => expect(within(section).getAllByRole('listitem')).toHaveLength(3));
    expect(within(section).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      '10BadkamerBewerken',
      '20KeukenBewerken',
      '30Schuur(inactief)Bewerken',
    ]);
  });

  it('renames, reorders and deactivates a room', async () => {
    const fetchMock = setup();
    renderWithProviders(<RoomsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Keuken' }));
    const form = screen.getByRole('form', { name: 'Bewerk Keuken' });

    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Keuken & bijkeuken' } });
    fireEvent.change(within(form).getByLabelText('Volgorde'), { target: { value: '5' } });
    fireEvent.click(within(form).getByLabelText('Actief'));
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() =>
      expect(bodies(fetchMock, 'PATCH')).toEqual([[`/api/rooms/${KEUKEN._id}`, { name: 'Keuken & bijkeuken', sortOrder: 5, active: false }]]),
    );
    expect(await screen.findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('adds a room, leaving the position to the server when empty', async () => {
    const fetchMock = setup();
    renderWithProviders(<RoomsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Ruimte toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe ruimte' });

    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(within(form).getByRole('alert')).toHaveTextContent('Vul een naam in.');

    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Zolder' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(bodies(fetchMock, 'POST')).toEqual([['/api/rooms', { name: 'Zolder' }]]));
  });

  it('deletes an empty room but blocks a room that still has tasks', async () => {
    const fetchMock = setup({
      '/api/tasks': [makeTask({ _id: 't1', name: 'Aanrecht', roomId: KEUKEN._id })],
      [`DELETE /api/rooms/${BADKAMER._id}`]: { deleted: true },
    });
    renderWithProviders(<RoomsSection />);

    expect(await screen.findByRole('button', { name: 'Verwijder Keuken' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Verwijder Badkamer' }));
    expect(screen.getByRole('heading', { name: 'Badkamer definitief verwijderen?' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Ruimte verwijderen' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => url === `/api/rooms/${BADKAMER._id}` && (init as RequestInit)?.method === 'DELETE')).toBe(true));
  });
});
