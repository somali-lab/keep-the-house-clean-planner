import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, householdRoutes, mockApi, page, problem, requestsTo, sequence, storeProfile } from '../../test/fixtures.ts';
import { makeRoomV2, makeSettings, makeTaskV2, renderWithProviders } from '../../test/render.tsx';
import { RoomsSection } from './RoomsSection.tsx';

const KEUKEN = makeRoomV2({ id: 'r00000000000000000000001', name: 'Keuken', sortOrder: 20, version: 4 });
const BADKAMER = makeRoomV2({ id: 'r00000000000000000000002', name: 'Badkamer', sortOrder: 10, version: 2 });
const SCHUUR = makeRoomV2({ id: 'r00000000000000000000003', name: 'Schuur', sortOrder: 30, active: false });

function setup(extra: Record<string, unknown> = {}) {
  storeProfile(ANNA.id);
  return mockApi({
    ...householdRoutes([ANNA, BRAM], makeSettings()),
    '/api/v2/rooms': page([KEUKEN, SCHUUR, BADKAMER]),
    '/api/v2/tasks': page([]),
    [`PATCH /api/v2/rooms/${KEUKEN.id}`]: { ...KEUKEN, name: 'Keuken & bijkeuken', sortOrder: 5, active: false, version: 5 },
    'POST /api/v2/rooms': makeRoomV2({ id: 'r00000000000000000000004', name: 'Zolder' }),
    ...extra,
  });
}

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

  it('renames, reorders and deactivates a room, with the version of the room as If-Match', async () => {
    const fetchMock = setup();
    renderWithProviders(<RoomsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Keuken' }));
    const form = screen.getByRole('form', { name: 'Bewerk Keuken' });

    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Keuken & bijkeuken' } });
    fireEvent.change(within(form).getByLabelText('Volgorde'), { target: { value: '5' } });
    fireEvent.click(within(form).getByLabelText('Actief'));
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() => expect(requestsTo(fetchMock, 'PATCH', `/api/v2/rooms/${KEUKEN.id}`)).toHaveLength(1));
    const [request] = requestsTo(fetchMock, 'PATCH', `/api/v2/rooms/${KEUKEN.id}`);
    expect(request!.body).toEqual({ name: 'Keuken & bijkeuken', sortOrder: 5, active: false });
    expect(request!.headers['if-match']).toBe('"4"');
    expect(await screen.findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('keeps the edit and says so when the room changed in the meantime (412)', async () => {
    setup({ [`PATCH /api/v2/rooms/${KEUKEN.id}`]: problem(412, 'precondition_failed', 'The room changed.') });
    renderWithProviders(<RoomsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Keuken' }));
    const form = screen.getByRole('form', { name: 'Bewerk Keuken' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Keuken 2' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    expect(await within(form).findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    expect(within(form).getByLabelText('Naam')).toHaveValue('Keuken 2');
  });

  it('sends the version that was read again when the person saves a second time after a 412', async () => {
    const url = `/api/v2/rooms/${KEUKEN.id}`;
    const fetchMock = setup({
      '/api/v2/rooms': sequence(page([KEUKEN, SCHUUR, BADKAMER]), page([{ ...KEUKEN, version: 9 }, SCHUUR, BADKAMER])),
      [`PATCH ${url}`]: sequence(() => problem(412, 'precondition_failed', 'Changed.'), { ...KEUKEN, version: 10 }),
    });
    renderWithProviders(<RoomsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Keuken' }));
    const form = screen.getByRole('form', { name: 'Bewerk Keuken' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Keuken 2' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await within(form).findByRole('alert');
    await waitFor(() => expect(requestsTo(fetchMock, 'GET', '/api/v2/rooms?limit=200').length).toBeGreaterThan(1));
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(requestsTo(fetchMock, 'PATCH', url)).toHaveLength(2));
    expect(requestsTo(fetchMock, 'PATCH', url).map((r) => r.headers['if-match'])).toEqual(['"4"', '"9"']);
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
    await waitFor(() => expect(requestsTo(fetchMock, 'POST', '/api/v2/rooms')).toHaveLength(1));
    expect(requestsTo(fetchMock, 'POST', '/api/v2/rooms')[0]!.body).toEqual({ name: 'Zolder' });
  });

  it('deletes an empty room with its version, but blocks a room that still has tasks', async () => {
    const fetchMock = setup({
      '/api/v2/tasks': page([makeTaskV2({ id: 't1', name: 'Aanrecht', roomId: KEUKEN.id })]),
      [`DELETE /api/v2/rooms/${BADKAMER.id}`]: { deleted: true },
    });
    renderWithProviders(<RoomsSection />);

    expect(await screen.findByRole('button', { name: 'Verwijder Keuken' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Verwijder Badkamer' }));
    expect(screen.getByRole('heading', { name: 'Badkamer definitief verwijderen?' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Ruimte verwijderen' }));
    await waitFor(() => expect(requestsTo(fetchMock, 'DELETE', `/api/v2/rooms/${BADKAMER.id}`)).toHaveLength(1));
    expect(requestsTo(fetchMock, 'DELETE', `/api/v2/rooms/${BADKAMER.id}`)[0]!.headers['if-match']).toBe('"2"');
  });

  it('says in the dialog that the room changed (412) and starts clean when it is opened again', async () => {
    setup({ [`DELETE /api/v2/rooms/${BADKAMER.id}`]: problem(412, 'precondition_failed', 'The room changed.') });
    renderWithProviders(<RoomsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Verwijder Badkamer' }));
    fireEvent.click(screen.getByRole('button', { name: 'Ruimte verwijderen' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');

    fireEvent.click(within(dialog).getByRole('button', { name: 'Annuleren' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Verwijder Badkamer' }));
    expect(within(await screen.findByRole('dialog')).queryByRole('alert')).not.toBeInTheDocument();
  });
});
