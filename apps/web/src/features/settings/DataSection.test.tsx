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
  fetchMock.mock.calls.filter(([u, init]) => u === '/api/v2/stats' && (init as RequestInit | undefined)?.method === 'DELETE');

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
    const fetchMock = mockApi({ [`POST /api/import/json`]: { replaced: { users: 2 }, auditAdded: 0 }, '/api/points/redemptions/count': { count: 0 }, '/api/badges': { badges: [] } });
    renderWithProviders(<DataSection />);

    choose(JSON.stringify(FILE));
    const dialog = await screen.findByRole('dialog', { name: 'Alle gegevens vervangen?' });
    expect(dialog).toHaveTextContent('"huishoudplanner-20260916.json" bevat 2 personen, 1 taken en 3 geplande taken.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Annuleren' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(importCalls(fetchMock)).toEqual([]);

    choose(JSON.stringify(FILE));
    const confirm = within(await screen.findByRole('dialog')).getByRole('button', { name: 'Alles vervangen' });
    await waitFor(() => expect(confirm).toBeEnabled());
    fireEvent.click(confirm);
    await waitFor(() => expect(importCalls(fetchMock)).toEqual([FILE]));
    expect(await screen.findByRole('status')).toHaveTextContent('Import voltooid: alle gegevens zijn vervangen.');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  describe('redemptions and an older file', () => {
    const COUNT = '/api/points/redemptions/count';
    const countCalls = (fetchMock: ReturnType<typeof vi.fn>) => fetchMock.mock.calls.filter(([u]) => u === COUNT);
    const importUrls = (fetchMock: ReturnType<typeof vi.fn>) =>
      fetchMock.mock.calls.filter(([u]) => String(u).startsWith('/api/import/json')).map(([u]) => String(u));

    it('warns with the count when the file is older than version 5, and only imports after it is acknowledged', async () => {
      storeProfile(ANNA._id);
      const fetchMock = mockApi({ [COUNT]: { count: 3 }, 'POST /api/import/json': { replaced: {}, auditAdded: 0 } });
      renderWithProviders(<DataSection />);
      choose(JSON.stringify({ ...FILE, schemaVersion: 4 }));
      const dialog = await screen.findByRole('dialog', { name: 'Alle gegevens vervangen?' });
      expect(await within(dialog).findByRole('alert')).toHaveTextContent(
        'Dit bestand is van een oudere versie (versie 4) en bevat geen inwisselingen. De 3 inwisselingen die er nu zijn, worden door deze import verwijderd.',
      );
      const confirm = within(dialog).getByRole('button', { name: 'Alles vervangen' });
      expect(confirm).toBeDisabled();
      fireEvent.click(confirm);
      expect(importUrls(fetchMock)).toEqual([]);

      fireEvent.click(within(dialog).getByRole('checkbox', { name: 'Ik begrijp dat 3 inwisselingen verloren gaan' }));
      expect(confirm).toBeEnabled();
      fireEvent.click(confirm);
      await waitFor(() => expect(importUrls(fetchMock)).toEqual([IMPORT_URL + '&acknowledgeRedemptions=true']));
    });

    it('asks for the acknowledgement again for the next file', async () => {
      storeProfile(ANNA._id);
      mockApi({ [COUNT]: { count: 1 } });
      renderWithProviders(<DataSection />);
      choose(JSON.stringify({ ...FILE, schemaVersion: 2 }));
      fireEvent.click(await screen.findByRole('checkbox'));
      fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Annuleren' }));
      choose(JSON.stringify({ ...FILE, schemaVersion: 2 }));
      expect(await screen.findByRole('checkbox')).not.toBeChecked();
    });

    it('shows no warning and no acknowledgement for an older file when there are no redemptions', async () => {
      storeProfile(ANNA._id);
      const fetchMock = mockApi({ [COUNT]: { count: 0 }, 'POST /api/import/json': { replaced: {}, auditAdded: 0 } });
      renderWithProviders(<DataSection />);
      choose(JSON.stringify({ ...FILE, schemaVersion: 4 }));
      const dialog = await screen.findByRole('dialog');
      await waitFor(() => expect(countCalls(fetchMock)).toHaveLength(1));
      expect(within(dialog).queryByRole('checkbox')).not.toBeInTheDocument();
      await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Alles vervangen' })).toBeEnabled());
      fireEvent.click(within(dialog).getByRole('button', { name: 'Alles vervangen' }));
      await waitFor(() => expect(importUrls(fetchMock)).toEqual([IMPORT_URL]));
    });

    it('does not ask for a version 5 file, which brings its own redemptions', async () => {
      storeProfile(ANNA._id);
      const fetchMock = mockApi({ [COUNT]: { count: 3 }, '/api/badges': { badges: [] } });
      renderWithProviders(<DataSection />);
      choose(JSON.stringify({ ...FILE, schemaVersion: 5 }));
      const dialog = await screen.findByRole('dialog');
      await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Alles vervangen' })).toBeEnabled());
      expect(within(dialog).queryByRole('checkbox')).not.toBeInTheDocument();
      expect(countCalls(fetchMock)).toEqual([]);
    });
  });

  describe('badges and an older file (ADR-0014)', () => {
    const importUrls = (fetchMock: ReturnType<typeof vi.fn>) =>
      fetchMock.mock.calls.filter(([u]) => String(u).startsWith('/api/import/json')).map(([u]) => String(u));
    const badges = (count: number) => ({ badges: Array.from({ length: count }, (_, i) => ({ _id: `b${i}` })) });

    it('warns with the count for a file older than version 6, and only imports after it is acknowledged', async () => {
      storeProfile(ANNA._id);
      const fetchMock = mockApi({ '/api/badges': badges(2), '/api/points/redemptions/count': { count: 0 }, 'POST /api/import/json': { replaced: {}, auditAdded: 0 } });
      renderWithProviders(<DataSection />);
      choose(JSON.stringify({ ...FILE, schemaVersion: 5 }));
      const dialog = await screen.findByRole('dialog', { name: 'Alle gegevens vervangen?' });
      expect(await within(dialog).findByRole('alert')).toHaveTextContent(
        'Dit bestand is van een oudere versie (versie 5) en bevat geen badges. De 2 badges die er nu zijn, met hun afbeeldingen, worden door deze import verwijderd.',
      );
      const confirm = within(dialog).getByRole('button', { name: 'Alles vervangen' });
      expect(confirm).toBeDisabled();
      fireEvent.click(confirm);
      expect(importUrls(fetchMock)).toEqual([]);
      fireEvent.click(within(dialog).getByRole('checkbox', { name: 'Ik begrijp dat 2 badges verloren gaan' }));
      expect(confirm).toBeEnabled();
      fireEvent.click(confirm);
      await waitFor(() => expect(importUrls(fetchMock)).toEqual([IMPORT_URL + '&acknowledgeBadges=true']));
    });

    it('asks for both acknowledgements when redemptions and badges would be lost', async () => {
      storeProfile(ANNA._id);
      const fetchMock = mockApi({ '/api/badges': badges(1), '/api/points/redemptions/count': { count: 3 }, 'POST /api/import/json': { replaced: {}, auditAdded: 0 } });
      renderWithProviders(<DataSection />);
      choose(JSON.stringify({ ...FILE, schemaVersion: 4 }));
      const dialog = await screen.findByRole('dialog');
      const confirm = within(dialog).getByRole('button', { name: 'Alles vervangen' });
      fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Ik begrijp dat 3 inwisselingen verloren gaan' }));
      expect(confirm).toBeDisabled();
      fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Ik begrijp dat 1 badges verloren gaan' }));
      expect(confirm).toBeEnabled();
      fireEvent.click(confirm);
      await waitFor(() => expect(importUrls(fetchMock)).toEqual([IMPORT_URL + '&acknowledgeRedemptions=true&acknowledgeBadges=true']));
    });

    it('shows nothing for a version 6 file or when there are no badges', async () => {
      storeProfile(ANNA._id);
      const fetchMock = mockApi({ '/api/badges': badges(0), 'POST /api/import/json': { replaced: {}, auditAdded: 0 } });
      renderWithProviders(<DataSection />);
      choose(JSON.stringify({ ...FILE, schemaVersion: 5 }));
      const dialog = await screen.findByRole('dialog');
      await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Alles vervangen' })).toBeEnabled());
      expect(within(dialog).queryByRole('checkbox')).not.toBeInTheDocument();
      fireEvent.click(within(dialog).getByRole('button', { name: 'Annuleren' }));
      choose(JSON.stringify({ ...FILE, schemaVersion: 6 }));
      const next = await screen.findByRole('dialog');
      expect(within(next).getByRole('button', { name: 'Alles vervangen' })).toBeEnabled();
      expect(within(next).queryByRole('checkbox')).not.toBeInTheDocument();
      expect(importUrls(fetchMock)).toEqual([]);
    });
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
        : new Response(String(input).endsWith('/redemptions/count') ? '{"count":0}' : '[]', { status: 200 }),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderWithProviders(<DataSection />);

    choose(JSON.stringify(FILE));
    const confirm = within(await screen.findByRole('dialog')).getByRole('button', { name: 'Alles vervangen' });
    // The confirmation waits for the number of redemptions an older file would remove.
    await waitFor(() => expect(confirm).toBeEnabled());
    fireEvent.click(confirm);
    expect(await screen.findByRole('alert')).toHaveTextContent('Dit bestand kan niet worden geïmporteerd.');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('resets completion and execution data only after confirmation', async () => {
    storeProfile(ANNA._id);
    const fetchMock = mockApi({ 'DELETE /api/v2/stats': { deletedOccurrences: 4, resetOccurrences: 3 } });
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
