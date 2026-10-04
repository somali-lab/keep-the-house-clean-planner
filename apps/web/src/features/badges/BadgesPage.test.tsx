import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { applyLanguage } from '../../i18n/runtime.ts';
import { ANNA, makeBadge, makeBadgeImage, mockApi, page, problem, storeProfile, v2Basics } from '../../test/fixtures.ts';
import { makeRoomV2, makeTaskV2, renderWithProviders } from '../../test/render.tsx';
import type { Badge } from './api.ts';
import { BadgesPage } from './BadgesPage.tsx';

const TOILET_TASK = makeTaskV2({ id: 't00000000000000000000001', name: 'Toilet schoonmaken', roomId: 'r1' });
const MOP_TASK = makeTaskV2({ id: 't00000000000000000000002', name: 'Vloer dweilen', roomId: 'r1' });
const OLD_TASK = makeTaskV2({ id: 't00000000000000000000003', name: 'Oude taak', roomId: 'r1', active: false });

const TOILET = makeBadge({
  id: 'b00000000000000000000001',
  name: 'Toiletjuffrouw',
  description: 'Het toilet vaak gedaan',
  rule: { type: 'executions', taskIds: [TOILET_TASK.id], threshold: 10 },
  image: makeBadgeImage('b00000000000000000000001'),
  version: 3,
});
const MOP = makeBadge({
  id: 'b00000000000000000000002',
  name: 'Dweilkampioen',
  rule: { type: 'minutes', taskIds: [], threshold: 300 },
  active: false,
  exampleKey: 'example:mop',
  version: 2,
});

const PNG = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0]);
const pngFile = (name = 'badge.png') => new File([PNG], name, { type: 'image/png' });

const STALE_MESSAGE = 'Deze gegevens zijn intussen door iemand anders gewijzigd. Controleer je wijziging en sla opnieuw op.';

function setup(extra: Record<string, unknown> = {}, badges: Badge[] = [TOILET, MOP]) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA],
    '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Badkamer' })]),
    '/api/v2/tasks': page([TOILET_TASK, MOP_TASK, OLD_TASK]),
    '/api/v2/badges': page(badges),
    ...v2Basics(),
    'POST /api/v2/badges': (init: RequestInit) => makeBadge({ id: 'b00000000000000000000009', ...JSON.parse(String(init.body)) }),
    [`PATCH /api/v2/badges/${TOILET.id}`]: (init: RequestInit) => ({ ...TOILET, ...JSON.parse(String(init.body)), version: 4 }),
    [`DELETE /api/v2/badges/${MOP.id}`]: { deleted: true },
    ...extra,
  });
}

/** The requests of one method: url, the parsed JSON body and the `If-Match` header. */
const sent = (fetchMock: ReturnType<typeof mockApi>, method: string) =>
  fetchMock.mock.calls
    .filter(([, init]) => (init as RequestInit | undefined)?.method === method)
    .map(([url, init]) => ({
      url,
      body: (init as RequestInit).body ? JSON.parse(String((init as RequestInit).body)) : undefined,
      ifMatch: ((init as RequestInit).headers as Record<string, string>)['if-match'],
    }));

const listReads = (fetchMock: ReturnType<typeof mockApi>) => fetchMock.mock.calls.filter(([url]) => url === '/api/v2/badges?limit=100').length;

/** An explicit JSON null anywhere in a body is refused by the API (400 validation_error), so it must not be sent unless documented. */
const nullKeys = (value: unknown, path = ''): string[] =>
  value === null
    ? [path]
    : typeof value === 'object' && value !== undefined
      ? Object.entries(value as Record<string, unknown>).flatMap(([key, item]) => nullKeys(item, path ? `${path}.${key}` : key))
      : [];

describe('BadgesPage', () => {
  it('lists the badges with their picture, rule and state in words', async () => {
    const fetchMock = setup();
    renderWithProviders(<BadgesPage />);
    expect(await screen.findByRole('heading', { level: 1, name: 'Badges' })).toBeInTheDocument();
    const rows = await screen.findAllByRole('listitem');
    expect(rows).toHaveLength(2);

    // The address of the picture is the one the server gives, with the hash that makes it cacheable for good.
    expect(within(rows[0]!).getByRole('img', { name: 'Toiletjuffrouw' })).toHaveAttribute('src', '/api/v2/badges/b00000000000000000000001/image?v=aaaaaaaaaaaa');
    expect(rows[0]).toHaveTextContent('10 uitvoeringen van Toilet schoonmaken');
    expect(rows[0]).toHaveTextContent('Het toilet vaak gedaan');
    // An inactive example without an uploaded image: the state is written, the picture is the named standard medal.
    expect(rows[1]).toHaveTextContent('Dweilkampioen');
    expect(rows[1]).toHaveTextContent('(inactief)');
    expect(rows[1]).toHaveTextContent('voorbeeld');
    expect(rows[1]).toHaveTextContent('300 minuten van alle taken');
    expect(within(rows[1]!).getByRole('img', { name: 'Dweilkampioen' })).not.toHaveAttribute('src');
    expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith('/api/badges'))).toBe(false);
  });

  it('follows the pages of the list up to the limit of 100 per page', async () => {
    const fetchMock = setup({
      '/api/v2/badges': (_init: RequestInit | undefined, url: string) =>
        url.includes('cursor=next') ? page([MOP]) : { items: [TOILET], nextCursor: 'next' },
    });
    renderWithProviders(<BadgesPage />);
    expect(await screen.findAllByRole('listitem')).toHaveLength(2);
    expect(fetchMock.mock.calls.map(([url]) => url).filter((url) => String(url).startsWith('/api/v2/badges?'))).toEqual([
      '/api/v2/badges?limit=100',
      '/api/v2/badges?limit=100&cursor=next',
    ]);
  });

  it('shows an empty state with the way to start', async () => {
    setup({}, []);
    renderWithProviders(<BadgesPage />);
    expect(await screen.findByText(/Er zijn nog geen badges/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Voorbeeldbadges toevoegen' })).toBeInTheDocument();
  });

  it('adds the example badges in the interface language and says what happened, also the second time', async () => {
    let created = [
      makeBadge({ id: 'b5', name: 'Toiletjuffrouw', active: false, exampleKey: 'example:toilet' }),
      makeBadge({ id: 'b6', name: 'Alles op tijd', exampleKey: 'example:on_time' }),
    ];
    const fetchMock = setup({ 'POST /api/v2/badges/examples': () => ({ created, skipped: 3 - created.length }) });
    renderWithProviders(<BadgesPage />);
    await screen.findAllByRole('listitem');
    fireEvent.click(screen.getByRole('button', { name: 'Voorbeeldbadges toevoegen' }));
    expect(await screen.findByText(/Voorbeeldbadges toegevoegd: 2./)).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('staan ze op inactief');
    expect(sent(fetchMock, 'POST').map(({ url, body }) => [url, body])).toEqual([['/api/v2/badges/examples', { language: 'nl' }]]);

    created = [];
    fireEvent.click(screen.getByRole('button', { name: 'Voorbeeldbadges toevoegen' }));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('De voorbeeldbadges zijn al toegevoegd.'));
  });

  it('sends the English language to the example action', async () => {
    applyLanguage('en');
    const fetchMock = setup({ 'POST /api/v2/badges/examples': { created: [], skipped: 3 } });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Add example badges' }));
    await waitFor(() => expect(sent(fetchMock, 'POST').map(({ url, body }) => [url, body])).toEqual([['/api/v2/badges/examples', { language: 'en' }]]));
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
    await waitFor(() => expect(sent(fetchMock, 'POST')).toHaveLength(1));
    const [request] = sent(fetchMock, 'POST');
    expect(request).toEqual({
      url: '/api/v2/badges',
      body: {
        name: 'Dweilheld',
        description: '',
        rule: { type: 'minutes', taskIds: [MOP_TASK.id], threshold: 120 },
        active: true,
        image: { contentType: 'image/png', data: btoa(String.fromCharCode(...PNG)) },
      },
      // A new badge has no version to match.
      ifMatch: undefined,
    });
    expect(nullKeys(request!.body)).toEqual([]);
    expect(await screen.findByRole('status')).toHaveTextContent('Badge opgeslagen.');
  });

  it('leaves the image and the tasks out of a new badge that has neither', async () => {
    const fetchMock = setup();
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Badge toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe badge' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Alles op tijd' } });
    fireEvent.change(within(form).getByLabelText('Regel'), { target: { value: 'onTimeWeeks' } });
    fireEvent.change(within(form).getByLabelText('Aantal weken'), { target: { value: '4' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(sent(fetchMock, 'POST')).toHaveLength(1));
    const [request] = sent(fetchMock, 'POST');
    expect(request!.body).toEqual({ name: 'Alles op tijd', description: '', rule: { type: 'onTimeWeeks', threshold: 4 }, active: true });
    expect(nullKeys(request!.body)).toEqual([]);
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
    expect(sent(fetchMock, 'POST')).toEqual([]);
  });

  it('refuses a picture of the wrong type or one that is too large, with the limits of the server, before it is sent', async () => {
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
    expect(within(form).queryByRole('img', { name: 'Voorbeeld van de gekozen afbeelding' })).not.toBeInTheDocument();

    // A save with a refused picture still pending is not sent; a good picture clears the message.
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Naam' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(sent(fetchMock, 'POST')).toEqual([]);
    fireEvent.change(input, { target: { files: [pngFile()] } });
    await within(form).findByRole('img', { name: 'Voorbeeld van de gekozen afbeelding' });
    expect(within(form).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows the refusal of the server for a file that only pretends to be an image', async () => {
    setup({
      'POST /api/v2/badges': () =>
        problem(400, 'validation_error', 'One or more validation errors occurred.', { errors: { 'image.data': ['unsupported_image_type'] } }),
    });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Badge toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe badge' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Naam' } });
    fireEvent.change(within(form).getByLabelText('Afbeelding kiezen'), { target: { files: [new File(['just text'], 'text.png', { type: 'image/png' })] } });
    await within(form).findByRole('img', { name: 'Voorbeeld van de gekozen afbeelding' });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Kies een PNG-, JPEG- of WebP-afbeelding.');
  });

  it('shows a server refusal of the size of the picture as a message', async () => {
    setup({
      'POST /api/v2/badges': () =>
        problem(400, 'validation_error', 'One or more validation errors occurred.', { errors: { 'image.data': ['image_too_large'] } }),
    });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Badge toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe badge' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Naam' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('De afbeelding is groter dan 256 KB.');
  });

  it('says that the limit of badges is reached', async () => {
    setup({ 'POST /api/v2/badges': () => problem(409, 'badge_limit', 'At most 100 badges can exist', { limit: 100 }) });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Badge toevoegen' }));
    const form = screen.getByRole('form', { name: 'Nieuwe badge' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Naam' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Er kunnen niet meer dan 100 badges bestaan.');
  });

  it('edits a badge with If-Match: changes the rule, deactivates it and removes its picture', async () => {
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

    await waitFor(() => expect(sent(fetchMock, 'PATCH')).toHaveLength(1));
    expect(sent(fetchMock, 'PATCH')[0]).toEqual({
      url: `/api/v2/badges/${TOILET.id}`,
      ifMatch: '"3"',
      body: {
        name: 'Toiletjuffrouw',
        description: 'Het toilet vaak gedaan',
        rule: { type: 'executions', taskIds: [TOILET_TASK.id], threshold: 2 },
        active: false,
        // Removing the picture is the one documented meaning of an explicit null.
        image: null,
      },
    });
  });

  it('leaves the picture out of the request when it was not touched, and sends the new version on a second save', async () => {
    let list = [TOILET, MOP];
    const fetchMock = setup({
      '/api/v2/badges': () => page(list),
      [`PATCH /api/v2/badges/${TOILET.id}`]: (init: RequestInit) => {
        const saved = { ...TOILET, ...JSON.parse(String(init.body)), version: 4 };
        list = [saved, MOP];
        return saved;
      },
    });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Toiletjuffrouw' }));
    fireEvent.click(within(screen.getByRole('form', { name: 'Bewerk Toiletjuffrouw' })).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(sent(fetchMock, 'PATCH')).toHaveLength(1));
    expect(sent(fetchMock, 'PATCH')[0]!.body).not.toHaveProperty('image');
    expect(nullKeys(sent(fetchMock, 'PATCH')[0]!.body)).toEqual([]);

    // The answer of the save put version 4 into the list: a second edit needs no re-read.
    await waitFor(() => expect(screen.queryByRole('form')).not.toBeInTheDocument());
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Toiletjuffrouw' }));
    fireEvent.click(within(screen.getByRole('form', { name: 'Bewerk Toiletjuffrouw' })).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(sent(fetchMock, 'PATCH')).toHaveLength(2));
    expect(sent(fetchMock, 'PATCH').map((call) => call.ifMatch)).toEqual(['"3"', '"4"']);
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

  it('deletes a badge only after a confirmation, with If-Match', async () => {
    const fetchMock = setup();
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Verwijder Dweilkampioen' }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveTextContent('Dweilkampioen verwijderen?');
    expect(sent(fetchMock, 'DELETE')).toEqual([]);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Badge verwijderen' }));
    await waitFor(() => expect(sent(fetchMock, 'DELETE')).toEqual([{ url: `/api/v2/badges/${MOP.id}`, body: undefined, ifMatch: '"2"' }]));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });
});

describe('BadgesPage — a stale version (412)', () => {
  it('keeps the unsaved edit, says so inside the form, reads the list again and saves with the new version when saved again', async () => {
    let list = [TOILET, MOP];
    let patches = 0;
    const fetchMock = setup({
      '/api/v2/badges': () => page(list),
      [`PATCH /api/v2/badges/${TOILET.id}`]: (init: RequestInit) => {
        patches += 1;
        if (patches > 1) return { ...TOILET, ...JSON.parse(String(init.body)), version: 8 };
        list = [{ ...TOILET, name: 'Toiletkoningin', version: 7 }, MOP];
        return problem(412, 'precondition_failed', 'stale');
      },
    });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Toiletjuffrouw' }));
    const form = screen.getByRole('form', { name: 'Bewerk Toiletjuffrouw' });
    fireEvent.change(within(form).getByLabelText('Aantal uitvoeringen', { selector: 'input' }), { target: { value: '25' } });
    const readsBefore = listReads(fetchMock);
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    // The message sits inside the form, next to the edit it is about.
    expect(await within(form).findByText(STALE_MESSAGE)).toBeInTheDocument();
    await waitFor(() => expect(listReads(fetchMock)).toBeGreaterThan(readsBefore));
    expect(within(form).getByLabelText('Aantal uitvoeringen', { selector: 'input' })).toHaveValue(25);

    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(patches).toBe(2));
    expect(sent(fetchMock, 'PATCH').map((call) => call.ifMatch)).toEqual(['"3"', '"7"']);
    expect(sent(fetchMock, 'PATCH')[1]!.body).toMatchObject({ rule: { threshold: 25 } });
    await waitFor(() => expect(screen.queryByRole('form')).not.toBeInTheDocument());
    expect(screen.queryByText(STALE_MESSAGE)).not.toBeInTheDocument();
  });

  it('does not show the stale message when the editor opens again', async () => {
    setup({ [`PATCH /api/v2/badges/${TOILET.id}`]: () => problem(412, 'precondition_failed', 'stale') });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Toiletjuffrouw' }));
    fireEvent.click(within(screen.getByRole('form', { name: 'Bewerk Toiletjuffrouw' })).getByRole('button', { name: 'Opslaan' }));
    expect(await screen.findByText(STALE_MESSAGE)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Annuleren' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Toiletjuffrouw' }));
    expect(screen.queryByText(STALE_MESSAGE)).not.toBeInTheDocument();
  });

  it('keeps the delete dialog open on a 412, with the message in it, and deletes with the new version when confirmed again', async () => {
    let list = [TOILET, MOP];
    let deletes = 0;
    const fetchMock = setup({
      '/api/v2/badges': () => page(list),
      [`DELETE /api/v2/badges/${MOP.id}`]: () => {
        deletes += 1;
        if (deletes > 1) return { deleted: true };
        list = [TOILET, { ...MOP, version: 5 }];
        return problem(412, 'precondition_failed', 'stale');
      },
    });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Verwijder Dweilkampioen' }));
    const readsBefore = listReads(fetchMock);
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Badge verwijderen' }));

    expect(await within(dialog).findByText(STALE_MESSAGE)).toBeInTheDocument();
    await waitFor(() => expect(listReads(fetchMock)).toBeGreaterThan(readsBefore));

    fireEvent.click(within(dialog).getByRole('button', { name: 'Badge verwijderen' }));
    await waitFor(() => expect(deletes).toBe(2));
    expect(sent(fetchMock, 'DELETE').map((call) => call.ifMatch)).toEqual(['"2"', '"5"']);
  });

  it('does not show the stale delete error when the dialog opens for another badge', async () => {
    setup({ [`DELETE /api/v2/badges/${MOP.id}`]: () => problem(412, 'precondition_failed', 'stale') });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Verwijder Dweilkampioen' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Badge verwijderen' }));
    expect(await screen.findByText(STALE_MESSAGE)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Annuleren' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Verwijder Toiletjuffrouw' }));
    expect(await screen.findByRole('dialog')).toHaveTextContent('Toiletjuffrouw verwijderen?');
    expect(screen.queryByText(STALE_MESSAGE)).not.toBeInTheDocument();
  });

  it('treats a 428 as a plain error, not as a stale edit', async () => {
    setup({ [`PATCH /api/v2/badges/${TOILET.id}`]: () => problem(428, 'precondition_required') });
    renderWithProviders(<BadgesPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Bewerk Toiletjuffrouw' }));
    const form = screen.getByRole('form', { name: 'Bewerk Toiletjuffrouw' });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('De badge kon niet worden opgeslagen.');
    expect(screen.queryByText(STALE_MESSAGE)).not.toBeInTheDocument();
  });
});
