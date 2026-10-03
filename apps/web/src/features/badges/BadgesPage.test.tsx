import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { applyLanguage } from '../../i18n/runtime.ts';
import { ANNA, makeBadge, makeBadgeImage, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeRoom, makeTask, renderWithProviders } from '../../test/render.tsx';
import { BadgesPage } from './BadgesPage.tsx';

const TOILET_TASK = makeTask({ _id: 't00000000000000000000001', name: 'Toilet schoonmaken', roomId: 'r1' });
const MOP_TASK = makeTask({ _id: 't00000000000000000000002', name: 'Vloer dweilen', roomId: 'r1' });
const OLD_TASK = makeTask({ _id: 't00000000000000000000003', name: 'Oude taak', roomId: 'r1', active: false });

const TOILET = makeBadge({
  _id: 'b00000000000000000000001',
  name: 'Toiletjuffrouw',
  description: 'Het toilet vaak gedaan',
  rule: { type: 'executions', taskIds: [TOILET_TASK._id], threshold: 10 },
  image: makeBadgeImage('b00000000000000000000001'),
});
const MOP = makeBadge({
  _id: 'b00000000000000000000002',
  name: 'Dweilkampioen',
  rule: { type: 'minutes', taskIds: [], threshold: 300 },
  active: false,
  exampleKey: 'example:mop',
});

const PNG = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0]);
const pngFile = (name = 'badge.png') => new File([PNG], name, { type: 'image/png' });

function setup(extra: Record<string, unknown> = {}, badges = [TOILET, MOP]) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA],
    '/api/rooms': [makeRoom({ _id: 'r1', name: 'Badkamer' })],
    '/api/tasks': [TOILET_TASK, MOP_TASK, OLD_TASK],
    '/api/badges': { badges },
    'POST /api/badges': (init: RequestInit) => makeBadge({ _id: 'b00000000000000000000009', ...JSON.parse(String(init.body)) }),
    [`PATCH /api/badges/${TOILET._id}`]: (init: RequestInit) => ({ ...TOILET, ...JSON.parse(String(init.body)) }),
    [`DELETE /api/badges/${MOP._id}`]: { deleted: true },
    ...extra,
  });
}

const bodies = (fetchMock: ReturnType<typeof mockApi>, method: string) =>
  fetchMock.mock.calls
    .filter(([, init]) => (init as RequestInit | undefined)?.method === method)
    .map(([url, init]) => [url, JSON.parse(String((init as RequestInit).body))]);

describe('BadgesPage', () => {
  it('lists the badges with their picture, rule and state in words', async () => {
    setup();
    renderWithProviders(<BadgesPage />);
    expect(await screen.findByRole('heading', { level: 1, name: 'Badges' })).toBeInTheDocument();
    const rows = await screen.findAllByRole('listitem');
    expect(rows).toHaveLength(2);

    expect(within(rows[0]!).getByRole('img', { name: 'Toiletjuffrouw' })).toHaveAttribute('src', TOILET.image!.url);
    expect(rows[0]).toHaveTextContent('10 uitvoeringen van Toilet schoonmaken');
    expect(rows[0]).toHaveTextContent('Het toilet vaak gedaan');
    // An inactive example without an uploaded image: the state is written, the picture is the named standard medal.
    expect(rows[1]).toHaveTextContent('Dweilkampioen');
    expect(rows[1]).toHaveTextContent('(inactief)');
    expect(rows[1]).toHaveTextContent('voorbeeld');
    expect(rows[1]).toHaveTextContent('300 minuten van alle taken');
    expect(within(rows[1]!).getByRole('img', { name: 'Dweilkampioen' })).not.toHaveAttribute('src');
  });

  it('shows an empty state with the way to start', async () => {
    setup({}, []);
    renderWithProviders(<BadgesPage />);
    expect(await screen.findByText(/Er zijn nog geen badges/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Voorbeeldbadges toevoegen' })).toBeInTheDocument();
  });

  it('adds the example badges in the interface language and says what happened, also the second time', async () => {
    let created = [makeBadge({ _id: 'b5', name: 'Toiletjuffrouw', active: false, exampleKey: 'example:toilet' }), makeBadge({ _id: 'b6', name: 'Alles op tijd', exampleKey: 'example:on_time' })];
    const fetchMock = setup({ 'POST /api/badges/examples': () => ({ created, skipped: 3 - created.length }) });
    renderWithProviders(<BadgesPage />);
    await screen.findAllByRole('listitem');
    fireEvent.click(screen.getByRole('button', { name: 'Voorbeeldbadges toevoegen' }));
    expect(await screen.findByText(/Voorbeeldbadges toegevoegd: 2./)).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('staan ze op inactief');
    expect(bodies(fetchMock, 'POST')).toEqual([['/api/badges/examples', { language: 'nl' }]]);

    created = [];
    fireEvent.click(screen.getByRole('button', { name: 'Voorbeeldbadges toevoegen' }));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('De voorbeeldbadges zijn al toegevoegd.'));
  });

  it('sends the English language to the example action', async () => {
    applyLanguage('en');
    const fetchMock = setup({ 'POST /api/badges/examples': { created: [], skipped: 3 } });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Add example badges' }));
    await waitFor(() => expect(bodies(fetchMock, 'POST')).toEqual([['/api/badges/examples', { language: 'en' }]]));
  });

  it('creates a badge with a name, a rule on chosen tasks and an uploaded picture that is previewed', async () => {
    const fetchMock = setup();
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Badge toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe badge' });

    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: '  Dweilheld ' } });
    fireEvent.change(within(form).getByLabelText('Regel'), { target: { value: 'minutes' } });
    fireEvent.change(within(form).getByLabelText('Aantal minuten'), { target: { value: '120' } });
    // Inactive tasks are not offered; the search narrows the list.
    expect(within(form).queryByLabelText('Oude taak')).not.toBeInTheDocument();
    fireEvent.change(within(form).getByLabelText('Zoek taken'), { target: { value: 'dweil' } });
    expect(within(form).queryByLabelText('Toilet schoonmaken')).not.toBeInTheDocument();
    fireEvent.click(within(form).getByLabelText('Vloer dweilen'));
    fireEvent.change(within(form).getByLabelText('Afbeelding kiezen'), { target: { files: [pngFile()] } });

    const preview = await within(form).findByRole('img', { name: 'Voorbeeld van de gekozen afbeelding' });
    expect(preview).toHaveAttribute('src', expect.stringMatching(/^data:image\/png;base64,/));
    expect(within(form).getByText('badge.png')).toBeInTheDocument();

    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() =>
      expect(bodies(fetchMock, 'POST')).toEqual([
        [
          '/api/badges',
          {
            name: 'Dweilheld',
            description: '',
            rule: { type: 'minutes', taskIds: [MOP_TASK._id], threshold: 120 },
            active: true,
            image: { contentType: 'image/png', data: btoa(String.fromCharCode(...PNG)) },
          },
        ],
      ]),
    );
    expect(await screen.findByRole('status')).toHaveTextContent('Badge opgeslagen.');
  });

  it('validates the form before sending anything', async () => {
    const fetchMock = setup();
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Badge toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe badge' });

    fireEvent.change(within(form).getByLabelText('Aantal uitvoeringen', { selector: 'input' }), { target: { value: '0' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(within(form).getByLabelText('Naam')).toHaveAccessibleDescription('Vul een naam in van maximaal 60 tekens.');
    expect(within(form).getByLabelText('Aantal uitvoeringen', { selector: 'input' })).toHaveAccessibleDescription('Vul een heel getal van 1 of hoger in.');
    expect(within(form).getByLabelText('Naam')).toBeInvalid();
    expect(bodies(fetchMock, 'POST')).toEqual([]);
  });

  it('refuses a picture of the wrong type, one that is too large and one that only pretends to be an image', async () => {
    const fetchMock = setup();
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Badge toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe badge' });
    const input = within(form).getByLabelText('Afbeelding kiezen');
    const alert = () => within(form).getByRole('alert');

    fireEvent.change(input, { target: { files: [new File(['<svg/>'], 'a.svg', { type: 'image/svg+xml' })] } });
    await waitFor(() => expect(alert()).toHaveTextContent('Kies een PNG-, JPEG- of WebP-afbeelding.'));

    fireEvent.change(input, { target: { files: [new File([new Uint8Array(256 * 1024 + 1)], 'big.png', { type: 'image/png' })] } });
    await waitFor(() => expect(alert()).toHaveTextContent('De afbeelding is groter dan 256 KB.'));

    fireEvent.change(input, { target: { files: [new File(['just text'], 'text.png', { type: 'image/png' })] } });
    await waitFor(() => expect(alert()).toHaveTextContent('Kies een PNG-, JPEG- of WebP-afbeelding.'));
    expect(within(form).queryByRole('img', { name: 'Voorbeeld van de gekozen afbeelding' })).not.toBeInTheDocument();

    // A save with a refused picture still pending is not sent; a good picture clears the message.
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Naam' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(bodies(fetchMock, 'POST')).toEqual([]);
    fireEvent.change(input, { target: { files: [pngFile()] } });
    await within(form).findByRole('img', { name: 'Voorbeeld van de gekozen afbeelding' });
    expect(within(form).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('edits a badge: changes the rule, deactivates it and removes its picture', async () => {
    const fetchMock = setup();
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Toiletjuffrouw' }));
    const form = screen.getByRole('form', { name: 'Bewerk Toiletjuffrouw' });
    expect(within(form).getByLabelText('Toilet schoonmaken')).toBeChecked();
    expect(within(form).getByLabelText('Vloer dweilen')).not.toBeChecked();
    expect(within(form).getByRole('img', { name: 'Voorbeeld van de gekozen afbeelding' })).toHaveAttribute('src', TOILET.image!.url);

    fireEvent.change(within(form).getByLabelText('Aantal uitvoeringen', { selector: 'input' }), { target: { value: '2' } });
    fireEvent.click(within(form).getByLabelText('Actief'));
    fireEvent.click(within(form).getByRole('button', { name: 'Afbeelding verwijderen' }));
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() =>
      expect(bodies(fetchMock, 'PATCH')).toEqual([
        [
          `/api/badges/${TOILET._id}`,
          {
            name: 'Toiletjuffrouw',
            description: 'Het toilet vaak gedaan',
            rule: { type: 'executions', taskIds: [TOILET_TASK._id], threshold: 2 },
            active: false,
            image: null,
          },
        ],
      ]),
    );
  });

  it('leaves the picture out of the request when it was not touched', async () => {
    const fetchMock = setup();
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Toiletjuffrouw' }));
    fireEvent.click(within(screen.getByRole('form', { name: 'Bewerk Toiletjuffrouw' })).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(bodies(fetchMock, 'PATCH')).toHaveLength(1));
    expect(bodies(fetchMock, 'PATCH')[0]![1]).not.toHaveProperty('image');
  });

  it('explains that the on-time-weeks rule needs bonuses and hides the task choice for it', async () => {
    setup();
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Badge toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe badge' });
    expect(within(form).getByText('Taken')).toBeInTheDocument();
    fireEvent.change(within(form).getByLabelText('Regel'), { target: { value: 'onTimeWeeks' } });
    expect(within(form).getByText(/hiervoor moeten bonussen zijn ingesteld/i)).toBeInTheDocument();
    expect(within(form).queryByLabelText('Zoek taken')).not.toBeInTheDocument();
    expect(within(form).getByLabelText('Aantal weken')).toBeInTheDocument();
  });

  it('deletes a badge only after a confirmation', async () => {
    const fetchMock = setup();
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Verwijder Dweilkampioen' }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveTextContent('Dweilkampioen verwijderen?');
    expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE')).toBe(false);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Badge verwijderen' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => (init as RequestInit | undefined)?.method === 'DELETE' && url === `/api/badges/${MOP._id}`)).toBe(true));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('shows a server refusal of the picture as a message', async () => {
    setup();
    mockApiFailure();
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Badge toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe badge' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Naam' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('De afbeelding is groter dan 256 KB.');
  });
});

/** Answers the create request with the refusal of the server for an oversized image. */
function mockApiFailure() {
  const original = globalThis.fetch;
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    if (init?.method === 'POST' && url === '/api/badges') {
      return new Response(JSON.stringify({ code: 'validation_error', details: [{ field: 'image.data', message: 'image_too_large' }] }), { status: 400 });
    }
    return original(input, init);
  }) as typeof fetch;
}
