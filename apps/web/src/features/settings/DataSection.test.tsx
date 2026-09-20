import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ANNA, mockApi, storeProfile } from '../../test/fixtures.ts';
import { renderWithProviders } from '../../test/render.tsx';
import { DataSection, readExport } from './DataSection.tsx';

const FILE = {
  schemaVersion: 1,
  exportedAt: '2026-09-16T08:00:00.000Z',
  collections: { users: [{}, {}], tasks: [{}], occurrences: [{}, {}, {}], auditLog: [] },
};
const IMPORT_URL = '/api/import/json?mode=replace&confirm=true';

function choose(content: string, name = 'huishoudplanner-20260916.json') {
  const input = screen.getByLabelText('JSON-bestand importeren');
  Object.defineProperty(input, 'files', { value: [new File([content], name, { type: 'application/json' })], configurable: true });
  fireEvent.change(input);
}

const importCalls = (fetchMock: ReturnType<typeof vi.fn>) =>
  fetchMock.mock.calls.filter(([u]) => u === IMPORT_URL).map(([, init]) => JSON.parse(String((init as RequestInit).body)));

const resetCalls = (fetchMock: ReturnType<typeof vi.fn>) =>
  fetchMock.mock.calls.filter(([u, init]) => u === '/api/stats' && (init as RequestInit | undefined)?.method === 'DELETE');

describe('readExport', () => {
  it('counts what a file contains and rejects files that are not exports', () => {
    expect(readExport('a.json', JSON.stringify(FILE))?.counts).toEqual({ users: 2, tasks: 1, occurrences: 3 });
    expect(readExport('a.json', '{not json')).toBeNull();
    expect(readExport('a.json', '[1,2]')).toBeNull();
    expect(readExport('a.json', '{"schemaVersion":1}')).toBeNull();
  });
});

describe('DataSection', () => {
  it('offers the JSON export as a download', () => {
    mockApi({});
    renderWithProviders(<DataSection />);
    const link = screen.getByRole('link', { name: 'Exporteren (JSON)' });
    expect(link).toHaveAttribute('href', '/api/export/json');
    expect(link).toHaveAttribute('download');
  });

  it('imports only after confirmation, and can be cancelled', async () => {
    storeProfile(ANNA._id);
    const fetchMock = mockApi({ [`POST /api/import/json`]: { replaced: { users: 2 }, auditAdded: 0 } });
    renderWithProviders(<DataSection />);

    choose(JSON.stringify(FILE));
    const dialog = await screen.findByRole('dialog', { name: 'Alle gegevens vervangen?' });
    expect(dialog).toHaveTextContent('"huishoudplanner-20260916.json" bevat 2 personen, 1 taken en 3 geplande taken.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Annuleren' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(importCalls(fetchMock)).toEqual([]);

    choose(JSON.stringify(FILE));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Alles vervangen' }));
    await waitFor(() => expect(importCalls(fetchMock)).toEqual([FILE]));
    expect(await screen.findByRole('status')).toHaveTextContent('Import voltooid: alle gegevens zijn vervangen.');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('rejects a file that is not an export without asking', async () => {
    const fetchMock = mockApi({});
    renderWithProviders(<DataSection />);
    choose('dit is geen json', 'boodschappen.txt');
    expect(await screen.findByRole('alert')).toHaveTextContent('Dit bestand kan niet worden geïmporteerd.');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(importCalls(fetchMock)).toEqual([]);
  });

  it('shows the server refusing an invalid file', async () => {
    storeProfile(ANNA._id);
    const fetchMock = vi.fn(async (input: RequestInfo | URL) =>
      String(input) === IMPORT_URL
        ? new Response(JSON.stringify({ code: 'validation_error', details: [] }), { status: 400 })
        : new Response('[]', { status: 200 }),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderWithProviders(<DataSection />);

    choose(JSON.stringify(FILE));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Alles vervangen' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Dit bestand kan niet worden geïmporteerd.');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('resets completion and execution data only after confirmation', async () => {
    storeProfile(ANNA._id);
    const fetchMock = mockApi({ 'DELETE /api/stats': { deletedOccurrences: 4, resetOccurrences: 3 } });
    renderWithProviders(<DataSection />);

    fireEvent.click(screen.getByRole('button', { name: 'Uitvoeringsgegevens resetten' }));
    let dialog = await screen.findByRole('dialog', { name: 'Alle gereedmeldingen en uitvoeringsgegevens resetten?' });
    expect(dialog).toHaveTextContent('Personen, ruimtes, taken en het actieve plan blijven bestaan.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Annuleren' }));
    expect(resetCalls(fetchMock)).toHaveLength(0);

    fireEvent.click(screen.getByRole('button', { name: 'Uitvoeringsgegevens resetten' }));
    dialog = await screen.findByRole('dialog', { name: 'Alle gereedmeldingen en uitvoeringsgegevens resetten?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Ja, alles resetten' }));

    await waitFor(() => expect(resetCalls(fetchMock)).toHaveLength(1));
    expect(await screen.findByRole('status')).toHaveTextContent('Gereedmeldingen en uitvoeringsgegevens zijn gereset.');
  });
});
