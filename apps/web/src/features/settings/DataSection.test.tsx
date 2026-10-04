import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, householdRoutes, mockApi, page, problem, requestsTo, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { DataSection, fileNameOf, MAX_IMPORT_BYTES, readExport } from './DataSection.tsx';

const FILE = {
  schemaVersion: 1,
  exportedAt: '2026-09-16T08:00:00.000Z',
  collections: { users: [{}, {}], tasks: [{}], occurrences: [{}, {}, {}], auditLog: [] },
};
const IMPORT_PATH = '/api/v2/import/json';
const IMPORT_URL = `${IMPORT_PATH}?mode=replace&confirm=true`;
const RESULT = { replaced: {}, auditAdded: 0, removedPointEntries: 0, removedRedemptions: 0, removedBadges: 0, removedBadgeAwards: 0 };

function choose(content: string, name = 'huishoudplanner-20260916.json', size?: number) {
  const input = screen.getByLabelText('JSON-bestand importeren');
  const file = new File([content], name, { type: 'application/json' });
  if (size !== undefined) Object.defineProperty(file, 'size', { value: size });
  Object.defineProperty(input, 'files', { value: [file], configurable: true });
  fireEvent.change(input);
}

function setup(extra: Record<string, unknown> = {}, profile = ANNA) {
  storeProfile(profile.id);
  return mockApi({ ...householdRoutes([ANNA, BRAM], makeSettings()), ...extra });
}

const importCalls = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.filter(([u]) => String(u).startsWith(IMPORT_PATH));
const importUrls = (fetchMock: ReturnType<typeof mockApi>) => importCalls(fetchMock).map(([u]) => String(u));
const importBodies = (fetchMock: ReturnType<typeof mockApi>) =>
  importCalls(fetchMock).map(([, init]) => JSON.parse(String((init as RequestInit).body)));
const resetCalls = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.filter(([u, init]) => u === '/api/v2/stats' && (init as RequestInit | undefined)?.method === 'DELETE');

afterEach(() => {
  vi.restoreAllMocks();
});

describe('readExport', () => {
  it('counts what a file contains and rejects files that are not exports', () => {
    expect(readExport('a.json', JSON.stringify(FILE))?.counts).toEqual({ users: 2, tasks: 1, occurrences: 3 });
    expect(readExport('a.json', '{not json')).toBeNull();
    expect(readExport('a.json', '[1,2]')).toBeNull();
    expect(readExport('a.json', '{"schemaVersion":1}')).toBeNull();
  });
});

describe('fileNameOf', () => {
  it('reads the name the server gives, and survives a name that is not valid percent-encoding', () => {
    expect(fileNameOf('attachment; filename="huishoudplanner-20260916.json"')).toBe('huishoudplanner-20260916.json');
    expect(fileNameOf("attachment; filename*=UTF-8''huis%20planner.json")).toBe('huis planner.json');
    expect(fileNameOf('attachment; filename="100%.json"')).toBe('100%.json');
    expect(fileNameOf(null)).toBe('huishoudplanner.json');
  });
});

describe('DataSection — export', () => {
  it('offers the export to administrators only', async () => {
    setup({}, BRAM);
    renderWithProviders(<DataSection />);
    await waitFor(() => expect(screen.getByLabelText('JSON-bestand importeren')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Exporteren (JSON)' })).not.toBeInTheDocument();
  });

  it('downloads the export with the profile header, named as the server names it', async () => {
    const fetchMock = setup({
      '/api/v2/export/json': () =>
        new Response('{"schemaVersion":6}', {
          status: 200,
          headers: { 'Content-Type': 'application/json', 'Content-Disposition': 'attachment; filename="huishoudplanner-20260916.json"' },
        }),
    });
    const createUrl = vi.fn(() => 'blob:export');
    const revokeUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { value: createUrl, configurable: true });
    Object.defineProperty(URL, 'revokeObjectURL', { value: revokeUrl, configurable: true });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      // The link has to be in the document when it is clicked.
      expect(document.body.contains(this)).toBe(true);
    });
    const timers = vi.spyOn(globalThis, 'setTimeout');
    renderWithProviders(<DataSection />);

    fireEvent.click(await screen.findByRole('button', { name: 'Exporteren (JSON)' }));
    await waitFor(() => expect(click).toHaveBeenCalledTimes(1));
    expect(requestsTo(fetchMock, 'GET', '/api/v2/export/json')[0]!.headers['x-profile-id']).toBe(ANNA.id);
    const anchor = click.mock.contexts[0] as HTMLAnchorElement;
    expect(anchor.download).toBe('huishoudplanner-20260916.json');
    expect(createUrl).toHaveBeenCalledTimes(1);
    // The object URL lives long enough for the browser to start the download.
    expect(revokeUrl).not.toHaveBeenCalled();
    expect(timers.mock.calls.some(([, delay]) => delay === 10_000)).toBe(true);
    expect(document.querySelector('a[download]')).toBeNull();
  });

  it('says so when the export fails', async () => {
    setup({ '/api/v2/export/json': () => problem(403, 'permission_denied', 'Administrators only.') });
    renderWithProviders(<DataSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Exporteren (JSON)' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('De export kon niet worden gemaakt.');
  });
});

describe('DataSection — import', () => {
  it('imports only after confirmation, and can be cancelled', async () => {
    const fetchMock = setup({ [`POST ${IMPORT_PATH}`]: RESULT });
    renderWithProviders(<DataSection />);

    choose(JSON.stringify(FILE));
    const dialog = await screen.findByRole('dialog', { name: 'Alle gegevens vervangen?' });
    expect(dialog).toHaveTextContent('"huishoudplanner-20260916.json" bevat 2 personen, 1 taken en 3 geplande taken.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Annuleren' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(importUrls(fetchMock)).toEqual([]);

    choose(JSON.stringify(FILE));
    const confirm = within(await screen.findByRole('dialog')).getByRole('button', { name: 'Alles vervangen' });
    await waitFor(() => expect(confirm).toBeEnabled());
    fireEvent.click(confirm);
    await waitFor(() => expect(importUrls(fetchMock)).toEqual([IMPORT_URL]));
    expect(importBodies(fetchMock)).toEqual([FILE]);
    expect(requestsTo(fetchMock, 'POST', IMPORT_URL)[0]!.headers['x-profile-id']).toBe(ANNA.id);
    expect(await screen.findByRole('status')).toHaveTextContent('Import voltooid: alle gegevens zijn vervangen.');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  describe('redemptions and an older file', () => {
    const COUNT = '/api/v2/points/redemptions/count';
    const countCalls = (fetchMock: ReturnType<typeof mockApi>) => fetchMock.mock.calls.filter(([u]) => u === COUNT);

    it('warns with the count when the file is older than version 5, and only imports after it is acknowledged', async () => {
      const fetchMock = setup({ [COUNT]: { count: '3' }, [`POST ${IMPORT_PATH}`]: RESULT, '/api/v2/badges': page([]) });
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
      setup({ [COUNT]: { count: 1 }, '/api/v2/badges': page([]) });
      renderWithProviders(<DataSection />);
      choose(JSON.stringify({ ...FILE, schemaVersion: 2 }));
      fireEvent.click(await screen.findByRole('checkbox'));
      fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Annuleren' }));
      choose(JSON.stringify({ ...FILE, schemaVersion: 2 }));
      expect(await screen.findByRole('checkbox')).not.toBeChecked();
    });

    it('shows no warning and no acknowledgement for an older file when there are no redemptions', async () => {
      const fetchMock = setup({ [COUNT]: { count: 0 }, [`POST ${IMPORT_PATH}`]: RESULT, '/api/v2/badges': page([]) });
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
      const fetchMock = setup({ [COUNT]: { count: 3 }, '/api/v2/badges': page([]) });
      renderWithProviders(<DataSection />);
      choose(JSON.stringify({ ...FILE, schemaVersion: 5 }));
      const dialog = await screen.findByRole('dialog');
      await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Alles vervangen' })).toBeEnabled());
      expect(within(dialog).queryByRole('checkbox')).not.toBeInTheDocument();
      expect(countCalls(fetchMock)).toEqual([]);
    });
  });

  describe('badges and an older file (ADR-0014)', () => {
    const badges = (count: number) => page(Array.from({ length: count }, (_, i) => ({ id: `b${i}` })));

    it('warns with the count for a file older than version 6, and only imports after it is acknowledged', async () => {
      const fetchMock = setup({ '/api/v2/badges': badges(2), '/api/v2/points/redemptions/count': { count: 0 }, [`POST ${IMPORT_PATH}`]: RESULT });
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
      const fetchMock = setup({ '/api/v2/badges': badges(1), '/api/v2/points/redemptions/count': { count: 3 }, [`POST ${IMPORT_PATH}`]: RESULT });
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
      const fetchMock = setup({ '/api/v2/badges': badges(0), [`POST ${IMPORT_PATH}`]: RESULT });
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
    const fetchMock = setup();
    renderWithProviders(<DataSection />);
    choose('dit is geen json', 'boodschappen.txt');
    expect(await screen.findByRole('alert')).toHaveTextContent('Dit bestand kan niet worden geïmporteerd.');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(importUrls(fetchMock)).toEqual([]);
  });

  it('lists where the server found the file invalid', async () => {
    setup({
      [`POST ${IMPORT_PATH}`]: () =>
        problem(400, 'validation_error', 'One or more fields are invalid.', {
          errors: { 'collections.users.0.color': ['invalid_color'], 'collections.tasks.2.name': ['required'] },
        }),
      '/api/v2/points/redemptions/count': { count: 0 },
      '/api/v2/badges': page([]),
    });
    renderWithProviders(<DataSection />);

    choose(JSON.stringify(FILE));
    const confirm = within(await screen.findByRole('dialog')).getByRole('button', { name: 'Alles vervangen' });
    await waitFor(() => expect(confirm).toBeEnabled());
    fireEvent.click(confirm);
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Dit bestand kan niet worden geïmporteerd.');
    expect(alert).toHaveTextContent('collections.users.0.color: invalid_color');
    expect(alert).toHaveTextContent('collections.tasks.2.name: required');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('asks for the acknowledgement the server asks for (409) and keeps the confirmation open', async () => {
    const fetchMock = setup({
      [`POST ${IMPORT_PATH}`]: (_init: RequestInit | undefined, url: string) =>
        url.includes('acknowledgeRedemptions=true')
          ? RESULT
          : problem(409, 'redemptions_would_be_removed', 'Older file.', { count: 4 }),
      '/api/v2/points/redemptions/count': { count: 0 },
      '/api/v2/badges': page([]),
    });
    renderWithProviders(<DataSection />);
    // A file of version 6 needs no acknowledgement of its own accord; the server decides.
    choose(JSON.stringify({ ...FILE, schemaVersion: 6 }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Alles vervangen' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('De 4 inwisselingen die er nu zijn');
    const confirm = within(dialog).getByRole('button', { name: 'Alles vervangen' });
    expect(confirm).toBeDisabled();
    fireEvent.click(within(dialog).getByRole('checkbox', { name: 'Ik begrijp dat 4 inwisselingen verloren gaan' }));
    fireEvent.click(confirm);
    await waitFor(() => expect(importUrls(fetchMock)).toEqual([IMPORT_URL, IMPORT_URL + '&acknowledgeRedemptions=true']));
    expect(await screen.findByRole('status')).toHaveTextContent('Import voltooid');
  });

  it('refuses a file over 200 MB before reading or sending it', async () => {
    const fetchMock = setup();
    renderWithProviders(<DataSection />);
    choose(JSON.stringify(FILE), 'groot.json', MAX_IMPORT_BYTES + 1);
    expect(await screen.findByRole('alert')).toHaveTextContent('Dit bestand is te groot om te importeren (200 MB).');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(importUrls(fetchMock)).toEqual([]);
  });

  it('says so when the file is too large', async () => {
    setup({
      [`POST ${IMPORT_PATH}`]: () => problem(413, 'payload_too_large', 'Too large.'),
      '/api/v2/points/redemptions/count': { count: 0 },
      '/api/v2/badges': page([]),
    });
    renderWithProviders(<DataSection />);
    choose(JSON.stringify(FILE));
    const confirm = within(await screen.findByRole('dialog')).getByRole('button', { name: 'Alles vervangen' });
    await waitFor(() => expect(confirm).toBeEnabled());
    fireEvent.click(confirm);
    expect(await screen.findByRole('alert')).toHaveTextContent('Dit bestand is te groot om te importeren (200 MB).');
  });
});

describe('DataSection — reset', () => {
  it('resets completion and execution data only after confirmation', async () => {
    const fetchMock = setup({ 'DELETE /api/v2/stats': { deletedOccurrences: 4, resetOccurrences: 3 } });
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
