import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, page, problem, storeProfile } from '../../test/fixtures.ts';
import { makeOccurrenceV2, makeSettings } from '../../test/render.tsx';
import { renderWithProviders } from '../../test/render.tsx';
import { CompletionManagementPage } from './CompletionManagementPage.tsx';

const KATTEN = 'o00000000000000000000001';

const record = (overrides: Parameters<typeof makeOccurrenceV2>[0]) =>
  makeOccurrenceV2({ status: 'done', statusBeforeCompletion: 'open', ...overrides });

function makeRecords() {
  return [
    record({
      id: KATTEN,
      taskNameSnapshot: 'Kattenmandjes',
      roomNameSnapshot: 'Hobbykamer Sven',
      date: '2026-09-27',
      completedAt: '2026-09-27T08:00:00.000Z',
      completedBy: ANNA.id,
    }),
    record({ id: 'o00000000000000000000002', taskNameSnapshot: 'Badkamer', date: '2026-09-26', completedAt: '2026-09-26T09:00:00.000Z', completedBy: BRAM.id }),
  ];
}

/** The requests of one method with their parsed body and headers, as the generated client sent them. */
const sent = (fetchMock: ReturnType<typeof mockApi>, method: string) =>
  fetchMock.mock.calls
    .filter(([, init]) => (init as RequestInit | undefined)?.method === method)
    .map(([url, init]) => ({
      url: String(url),
      body: (init as RequestInit).body ? JSON.parse(String((init as RequestInit).body)) : undefined,
      headers: (init as RequestInit).headers as Record<string, string>,
    }));

describe('CompletionManagementPage', () => {
  it('lists the done occurrences of the range from the v2 list and shows who did what when', async () => {
    storeProfile(ANNA.id);
    const fetchMock = mockApi({ '/api/v2/users': page([ANNA, BRAM]), '/api/v2/settings': makeSettings(), '/api/v2/occurrences': page(makeRecords()) });
    renderWithProviders(<CompletionManagementPage now={new Date('2026-09-27T12:00:00.000Z')} />, { headerReset: true });

    const row = (await screen.findByText('Kattenmandjes')).closest('li')!;
    expect(row).toHaveTextContent('Hobbykamer Sven');
    expect(row).toHaveTextContent('Anna');
    expect(row).toHaveTextContent('27 september 2026');
    const list = fetchMock.mock.calls.map(([url]) => String(url)).find((url) => url.startsWith('/api/v2/occurrences'))!;
    const query = new URL(list, 'http://localhost').searchParams;
    expect(Object.fromEntries(query)).toEqual({ from: '2026-08-30', to: '2026-09-27', status: 'done', limit: '100', order: 'desc' });
    // The server answers newest day first (order=desc) and the page shows the rows as received.
    expect(screen.getAllByRole('listitem').map((li) => li.querySelector('p')?.textContent)).toEqual(['Kattenmandjes', 'Badkamer']);
  });

  const occurrenceUrls = (fetchMock: ReturnType<typeof mockApi>) =>
    fetchMock.mock.calls.map(([url]) => String(url)).filter((url) => url.startsWith('/api/v2/occurrences'));

  it('shows the first page only, asks for the next page with the cursor on request and says how much is loaded', async () => {
    storeProfile(ANNA.id);
    const [first, second] = makeRecords();
    const fetchMock = mockApi({
      '/api/v2/users': page([ANNA, BRAM]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/occurrences': (_init: RequestInit | undefined, url: string) =>
        url.includes('cursor=more') ? page([second]) : { items: [first], nextCursor: 'more' },
    });
    renderWithProviders(<CompletionManagementPage now={new Date('2026-09-27T12:00:00.000Z')} />);
    expect(await screen.findByText('Kattenmandjes')).toBeInTheDocument();
    expect(screen.queryByText('Badkamer')).not.toBeInTheDocument();
    expect(occurrenceUrls(fetchMock)).toEqual(['/api/v2/occurrences?from=2026-08-30&to=2026-09-27&status=done&limit=100&order=desc']);
    expect(screen.getByText('1 getoond')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Meer laden' }));
    expect(await screen.findByText('Badkamer')).toBeInTheDocument();
    expect(occurrenceUrls(fetchMock)).toEqual([
      '/api/v2/occurrences?from=2026-08-30&to=2026-09-27&status=done&limit=100&order=desc',
      '/api/v2/occurrences?from=2026-08-30&to=2026-09-27&status=done&limit=100&order=desc&cursor=more',
    ]);
    // Load more appends older rows at the bottom.
    expect(screen.getAllByRole('listitem').map((li) => li.querySelector('p')?.textContent)).toEqual(['Kattenmandjes', 'Badkamer']);
    expect(screen.queryByRole('button', { name: 'Meer laden' })).not.toBeInTheDocument();
    expect(screen.getByText('Alles geladen (2)')).toBeInTheDocument();
    // The status is announced politely.
    expect(screen.getByText('Alles geladen (2)').closest('[aria-live]')).toHaveAttribute('aria-live', 'polite');
  });

  it('starts over on the first page when the range changes', async () => {
    storeProfile(ANNA.id);
    const [first, second] = makeRecords();
    const fetchMock = mockApi({
      '/api/v2/users': page([ANNA, BRAM]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/occurrences': (_init: RequestInit | undefined, url: string) =>
        url.includes('cursor=more') ? page([second]) : { items: [first], nextCursor: 'more' },
    });
    renderWithProviders(<CompletionManagementPage now={new Date('2026-09-27T12:00:00.000Z')} />);
    await screen.findByText('Kattenmandjes');
    fireEvent.click(screen.getByRole('button', { name: 'Meer laden' }));
    await screen.findByText('Badkamer');

    fireEvent.change(screen.getByLabelText('Vanaf'), { target: { value: '2026-09-01' } });
    await waitFor(() => expect(screen.queryByText('Badkamer')).not.toBeInTheDocument());
    expect(await screen.findByText('Kattenmandjes')).toBeInTheDocument();
    expect(occurrenceUrls(fetchMock).at(-1)).toBe('/api/v2/occurrences?from=2026-09-01&to=2026-09-27&status=done&limit=100&order=desc');
    expect(screen.getByRole('button', { name: 'Meer laden' })).toBeInTheDocument();
  });

  it('refreshes the loaded pages after a deletion, starting again from the first page', async () => {
    storeProfile(ANNA.id);
    const [first, second] = makeRecords();
    let deleted = false;
    const fetchMock = mockApi({
      '/api/v2/users': page([ANNA, BRAM]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/occurrences': (_init: RequestInit | undefined, url: string) => {
        if (deleted) return page([second]);
        return url.includes('cursor=more') ? page([second]) : { items: [first], nextCursor: 'more' };
      },
      [`DELETE /api/v2/occurrences/${KATTEN}`]: () => {
        deleted = true;
        return { deleted: true };
      },
    });
    renderWithProviders(<CompletionManagementPage now={new Date('2026-09-27T12:00:00.000Z')} />);
    const row = (await screen.findByText('Kattenmandjes')).closest('li')!;
    fireEvent.click(screen.getByRole('button', { name: 'Meer laden' }));
    await screen.findByText('Badkamer');

    fireEvent.click(within(row).getByRole('button', { name: 'Gereedmelding van Kattenmandjes verwijderen' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Definitief verwijderen' }));

    await waitFor(() => expect(screen.queryByText('Kattenmandjes')).not.toBeInTheDocument());
    expect(screen.getByText('Badkamer')).toBeInTheDocument();
    expect(screen.getByText('Alles geladen (1)')).toBeInTheDocument();
    expect(occurrenceUrls(fetchMock).at(-1)).toBe('/api/v2/occurrences?from=2026-08-30&to=2026-09-27&status=done&limit=100&order=desc');
  });

  it('edits through the completion endpoint and permanently deletes after confirmation, both without If-Match', async () => {
    storeProfile(ANNA.id);
    let records = makeRecords();
    const fetchMock = mockApi({
      '/api/v2/users': page([ANNA, BRAM]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/occurrences': () => page(records),
      [`POST /api/v2/occurrences/${KATTEN}/completion`]: (init: RequestInit | undefined) => {
        const body = JSON.parse(String(init?.body));
        records = records.map((item) => (item.id === KATTEN ? { ...item, date: body.date, completedAt: body.completedAt, completedBy: body.completedBy } : item));
        return records[0];
      },
      [`DELETE /api/v2/occurrences/${KATTEN}`]: () => {
        records = records.filter((item) => item.id !== KATTEN);
        return { deleted: true };
      },
    });

    renderWithProviders(<CompletionManagementPage now={new Date('2026-09-27T12:00:00.000Z')} />, { headerReset: true });
    expect(await screen.findByRole('heading', { name: 'Gereedmeldingen beheren' })).toBeInTheDocument();
    const row = (await screen.findByText('Kattenmandjes')).closest('li')!;
    fireEvent.click(within(row).getByRole('button', { name: 'Kattenmandjes bewerken' }));

    const editDialog = await screen.findByRole('dialog', { name: 'Kattenmandjes bewerken' });
    fireEvent.change(within(editDialog).getByLabelText('Taakdatum'), { target: { value: '2026-09-28' } });
    fireEvent.change(within(editDialog).getByLabelText('Gereed op'), { target: { value: '2026-09-28T11:30' } });
    fireEvent.change(within(editDialog).getByLabelText('Uitgevoerd door'), { target: { value: BRAM.id } });
    fireEvent.click(within(editDialog).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() => expect(sent(fetchMock, 'POST')).toHaveLength(1));
    const [edit] = sent(fetchMock, 'POST');
    expect(edit!.url).toBe(`/api/v2/occurrences/${KATTEN}/completion`);
    // Exactly the three fields, the instant as an ISO string; an intent endpoint carries no If-Match.
    expect(edit!.body).toEqual({ date: '2026-09-28', completedAt: new Date('2026-09-28T11:30').toISOString(), completedBy: BRAM.id });
    expect(edit!.headers['if-match']).toBeUndefined();
    expect(await screen.findByRole('status')).toHaveTextContent('De gereedmelding is bijgewerkt.');

    const updatedRow = (await screen.findByText('Kattenmandjes')).closest('li')!;
    fireEvent.click(within(updatedRow).getByRole('button', { name: 'Gereedmelding van Kattenmandjes verwijderen' }));
    const deleteDialog = await screen.findByRole('dialog', { name: 'Kattenmandjes verwijderen?' });
    expect(deleteDialog).toHaveTextContent('Ook deze geplande taakinstantie verdwijnt');
    expect(sent(fetchMock, 'DELETE')).toHaveLength(0);
    fireEvent.click(within(deleteDialog).getByRole('button', { name: 'Definitief verwijderen' }));

    await waitFor(() => expect(screen.queryByText('Kattenmandjes')).not.toBeInTheDocument());
    expect(sent(fetchMock, 'DELETE').map(({ url, body, headers }) => [url, body, headers['if-match']])).toEqual([[`/api/v2/occurrences/${KATTEN}`, undefined, undefined]]);
    expect(screen.getByRole('button', { name: 'Filters van dit scherm resetten' })).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Vanaf'), { target: { value: '2026-09-01' } });
    expect(window.localStorage.getItem(`huishoudplanner.filters.${ANNA.id}.completions.from`)).toBe('"2026-09-01"');
    fireEvent.click(screen.getByRole('button', { name: 'Filters van dit scherm resetten' }));
    expect(screen.getByLabelText('Vanaf')).toHaveValue('2026-08-30');
  });

  it('shows a refused edit inside the dialog and keeps the typed values', async () => {
    storeProfile(ANNA.id);
    mockApi({
      '/api/v2/users': page([ANNA, BRAM]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/occurrences': page(makeRecords()),
      [`POST /api/v2/occurrences/${KATTEN}/completion`]: () => problem(409, 'cycle_not_generated', 'No cycle for that day', { date: '2026-12-01' }),
    });
    renderWithProviders(<CompletionManagementPage now={new Date('2026-09-27T12:00:00.000Z')} />);
    const row = (await screen.findByText('Kattenmandjes')).closest('li')!;
    fireEvent.click(within(row).getByRole('button', { name: 'Kattenmandjes bewerken' }));
    const dialog = await screen.findByRole('dialog', { name: 'Kattenmandjes bewerken' });
    fireEvent.change(within(dialog).getByLabelText('Taakdatum'), { target: { value: '2026-12-01' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Opslaan' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('De gereedmelding kon niet worden bijgewerkt.');
    expect(within(dialog).getByLabelText('Taakdatum')).toHaveValue('2026-12-01');
  });

  it('shows a failed delete inside its dialog', async () => {
    storeProfile(ANNA.id);
    mockApi({
      '/api/v2/users': page([ANNA, BRAM]),
      '/api/v2/settings': makeSettings(),
      '/api/v2/occurrences': page(makeRecords()),
      [`DELETE /api/v2/occurrences/${KATTEN}`]: () => problem(409, 'invalid_transition', 'Not done'),
    });
    renderWithProviders(<CompletionManagementPage now={new Date('2026-09-27T12:00:00.000Z')} />);
    const row = (await screen.findByText('Kattenmandjes')).closest('li')!;
    fireEvent.click(within(row).getByRole('button', { name: 'Gereedmelding van Kattenmandjes verwijderen' }));
    const dialog = await screen.findByRole('dialog', { name: 'Kattenmandjes verwijderen?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Definitief verwijderen' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('De gereedmelding kon niet worden verwijderd.');
  });
});
