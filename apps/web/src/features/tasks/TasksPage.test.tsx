import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, mockApi, page, problem, storeProfile, v2Basics } from '../../test/fixtures.ts';
import { makeRoomV2, makeSettings, makeTaskV2, renderWithProviders } from '../../test/render.tsx';
import { TasksPage } from './TasksPage.tsx';

const keuken = makeRoomV2({ id: 'r1', name: 'Keuken', sortOrder: 10 });
const badkamer = makeRoomV2({ id: 'r2', name: 'Badkamer', sortOrder: 20 });

const tasks = [
  makeTaskV2({ id: 't1', name: 'Vloer dweilen', roomId: 'r1', intervalKey: '1w', durationMinutes: 20, version: 2 }),
  makeTaskV2({ id: 't2', name: 'Aanrecht', roomId: 'r1', intervalKey: 'daily', durationMinutes: 5, defaultAssigneeId: ANNA._id, version: 3 }),
  makeTaskV2({ id: 't3', name: 'Douche', roomId: 'r2', intervalKey: '2wk', durationMinutes: 30, version: 5 }),
  makeTaskV2({ id: 't4', name: 'Oude klus', roomId: 'r2', active: false }),
];

function setup(extraRoutes: Record<string, unknown> = {}) {
  for (const key of Object.keys(localStorage)) if (key.startsWith('huishoudplanner.filters.')) localStorage.removeItem(key);
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/v2/rooms': page([badkamer, keuken]),
    '/api/v2/tasks': page(tasks),
    ...v2Basics(),
    '/api/settings': makeSettings(),
    ...extraRoutes,
  });
}

function bodyOf(fetchMock: ReturnType<typeof mockApi>, method: string, url: string): unknown {
  const call = fetchMock.mock.calls.find(([u, init]) => u === url && (init as RequestInit | undefined)?.method === method);
  return call ? JSON.parse(String((call[1] as RequestInit).body)) : undefined;
}

function headerOf(fetchMock: ReturnType<typeof mockApi>, method: string, url: string, name: string): string | undefined {
  const call = fetchMock.mock.calls.find(([u, init]) => u === url && (init as RequestInit | undefined)?.method === method);
  return call ? ((call[1] as RequestInit).headers as Record<string, string>)[name] : undefined;
}

async function expandAllRooms() {
  fireEvent.click(await screen.findByRole('button', { name: 'Alles uitklappen' }));
}

describe('TasksPage — grouping', () => {
  beforeEach(() => {
    setup();
  });

  it('lists active tasks grouped per room in room order, with interval, duration and assignee', async () => {
    renderWithProviders(<TasksPage />);
    expect(await screen.findByRole('link', { name: 'Takenlijst als PDF' })).toHaveAttribute(
      'href',
      '/api/export/pdf/tasks',
    );
    expect(screen.getByRole('complementary', { name: 'AI-assistent' })).toBeInTheDocument();
    const sections = await screen.findAllByRole('region');
    expect(sections.map((s) => within(s).getByRole('heading', { level: 2 }).textContent)).toEqual(['Keuken', 'Badkamer']);
    await expandAllRooms();

    const keukenItems = within(sections[0]!).getAllByRole('listitem').map((li) => li.textContent);
    expect(keukenItems[0]).toContain('Aanrecht');
    expect(keukenItems[0]).toContain('Dagelijks · 5 min · Anna');
    expect(keukenItems[1]).toContain('Vloer dweilen');
    expect(keukenItems[1]).toContain('1x per week · 20 min · Wie dan ook');

    expect(screen.queryByText('Oude klus')).not.toBeInTheDocument();
  });

  it('shows inactive tasks when asked', async () => {
    renderWithProviders(<TasksPage />);
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Toon inactieve taken' }));
    await expandAllRooms();
    const row = (await screen.findByText('Oude klus')).closest('li')!;
    expect(row).toHaveTextContent('Inactief');
    expect(within(row).getByRole('button', { name: 'Oude klus activeren' })).toBeInTheDocument();
  });

  it('links each task to its history', async () => {
    renderWithProviders(<TasksPage />);
    await expandAllRooms();
    const row = (await screen.findByText('Douche')).closest('li')!;
    expect(within(row).getByRole('link', { name: 'Geschiedenis' })).toHaveAttribute(
      'href',
      '/manage/history?entity=task&entityId=t3',
    );
  });

  it('filters the list by room', async () => {
    renderWithProviders(<TasksPage />, { headerReset: true });
    expect(await screen.findByRole('button', { name: 'Filters van dit scherm resetten' })).toBeDisabled();
    fireEvent.change(await screen.findByLabelText('Filter op ruimte'), { target: { value: 'r2' } });
    const sections = screen.getAllByRole('region');
    expect(sections).toHaveLength(1);
    expect(within(sections[0]!).getByRole('heading', { level: 2 })).toHaveTextContent('Badkamer');
    expect(screen.queryByText('Aanrecht')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Filters van dit scherm resetten' }));
    expect(screen.getByLabelText('Filter op ruimte')).toHaveValue('all');
  });

  it('starts with every room collapsed and can expand one room or all rooms', async () => {
    renderWithProviders(<TasksPage />);
    const kitchenToggle = await screen.findByRole('button', { name: 'Keuken uitklappen' });
    expect(screen.queryByText('Aanrecht')).not.toBeInTheDocument();
    expect(screen.queryByText('Douche')).not.toBeInTheDocument();

    fireEvent.click(kitchenToggle);
    expect(await screen.findByText('Aanrecht')).toBeInTheDocument();
    expect(screen.queryByText('Douche')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Alles inklappen' }));
    expect(screen.queryByText('Aanrecht')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Alles uitklappen' }));
    expect(await screen.findByText('Aanrecht')).toBeInTheDocument();
    expect(screen.getByText('Douche')).toBeInTheDocument();
  });
});

describe('TasksPage — form validation', () => {
  it('requires a duration and does not call the API without one', async () => {
    const fetchMock = setup({ 'POST /api/v2/tasks': makeTaskV2({ id: 't9', name: 'Ramen', roomId: 'r1' }) });
    renderWithProviders(<TasksPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Nieuwe taak' }));

    const form = screen.getByRole('form', { name: 'Nieuwe taak' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Ramen' } });
    fireEvent.change(within(form).getByLabelText('Ruimte'), { target: { value: 'r1' } });
    fireEvent.change(within(form).getByLabelText('Interval'), { target: { value: '4wk' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    const duration = within(form).getByLabelText('Duur (minuten)');
    expect(await within(form).findByRole('alert')).toHaveTextContent('Vul de duur in minuten in.');
    expect(duration).toHaveAttribute('aria-invalid', 'true');
    expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'POST')).toBe(false);

    fireEvent.change(duration, { target: { value: '0' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('De duur moet een heel aantal minuten van minimaal 1 zijn.');

    fireEvent.change(duration, { target: { value: '25' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v2/tasks')).toEqual({
      name: 'Ramen',
      roomId: 'r1',
      intervalKey: '4wk',
      durationMinutes: 25,
      defaultAssigneeId: null,
      notes: '',
      tags: [],
    }));
    await waitFor(() => expect(screen.queryByRole('form')).not.toBeInTheDocument());
  });

  it('flags every missing required field at once', async () => {
    setup();
    renderWithProviders(<TasksPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Nieuwe taak' }));
    const form = screen.getByRole('form', { name: 'Nieuwe taak' });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    const alerts = await within(form).findAllByRole('alert');
    expect(alerts.map((a) => a.textContent)).toEqual([
      'Vul een naam in.',
      'Kies een ruimte.',
      'Kies een interval.',
      'Vul de duur in minuten in.',
    ]);
  });

  it('prefills the room when adding from a room section and edits existing tasks via PATCH', async () => {
    const fetchMock = setup({ 'PATCH /api/v2/tasks/t3': makeTaskV2({ id: 't3', name: 'Douche', roomId: 'r2' }) });
    renderWithProviders(<TasksPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Taak toevoegen aan Badkamer' }));
    expect(within(screen.getByRole('form')).getByLabelText('Ruimte')).toHaveValue('r2');
    fireEvent.click(within(screen.getByRole('form')).getByRole('button', { name: 'Annuleren' }));

    await expandAllRooms();
    fireEvent.click(screen.getByRole('button', { name: 'Douche bewerken' }));
    const form = screen.getByRole('form', { name: 'Douche bewerken' });
    expect(within(form).getByLabelText('Duur (minuten)')).toHaveValue(30);
    fireEvent.change(within(form).getByLabelText('Standaard uitvoerder'), { target: { value: BRAM._id } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() =>
      expect(bodyOf(fetchMock, 'PATCH', '/api/v2/tasks/t3')).toEqual({
        name: 'Douche',
        roomId: 'r2',
        intervalKey: '2wk',
        durationMinutes: 30,
        points: 15,
        defaultAssigneeId: BRAM._id,
        notes: '',
        tags: [],
      }),
    );
  });

  it('refuses to save an existing task with an empty points field instead of sending nothing', async () => {
    const fetchMock = setup({ 'PATCH /api/v2/tasks/t3': makeTaskV2({ id: 't3', name: 'Douche', roomId: 'r2' }) });
    renderWithProviders(<TasksPage />);
    await expandAllRooms();
    fireEvent.click(await screen.findByRole('button', { name: 'Douche bewerken' }));
    const form = screen.getByRole('form', { name: 'Douche bewerken' });
    fireEvent.change(within(form).getByLabelText('Punten'), { target: { value: '' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Vul de punten in.');
    expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'PATCH')).toBe(false);
  });

  it('sends the points typed for a new task and leaves the field out otherwise, so the server computes the default', async () => {
    const fetchMock = setup({ 'POST /api/v2/tasks': makeTaskV2({ id: 't9', name: 'Ramen', roomId: 'r1' }) });
    renderWithProviders(<TasksPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Nieuwe taak' }));
    const form = screen.getByRole('form', { name: 'Nieuwe taak' });
    expect(within(form).getByLabelText('Punten')).not.toHaveAttribute('placeholder');
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Ramen' } });
    fireEvent.change(within(form).getByLabelText('Ruimte'), { target: { value: 'r1' } });
    fireEvent.change(within(form).getByLabelText('Interval'), { target: { value: '4wk' } });
    fireEvent.change(within(form).getByLabelText('Duur (minuten)'), { target: { value: '25' } });
    fireEvent.change(within(form).getByLabelText('Punten'), { target: { value: '0' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v2/tasks')).toMatchObject({ durationMinutes: 25, points: 0 }));
  });

  it('marks the field the server refused, from the errors member of the problem', async () => {
    setup({
      'POST /api/v2/tasks': () => problem(400, 'validation_error', 'One or more fields are invalid.', { errors: { roomId: ['inactive_room'] } }),
    });
    renderWithProviders(<TasksPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Nieuwe taak' }));
    const form = screen.getByRole('form', { name: 'Nieuwe taak' });
    fireEvent.change(within(form).getByLabelText('Naam'), { target: { value: 'Ramen' } });
    fireEvent.change(within(form).getByLabelText('Ruimte'), { target: { value: 'r1' } });
    fireEvent.change(within(form).getByLabelText('Interval'), { target: { value: '4wk' } });
    fireEvent.change(within(form).getByLabelText('Duur (minuten)'), { target: { value: '25' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Deze waarde is niet geldig.');
    expect(within(form).getByLabelText('Ruimte')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('form', { name: 'Nieuwe taak' })).toBeInTheDocument();
  });
});

describe('TasksPage — actions', () => {
  it('deactivates a single task and runs bulk actions per room', async () => {
    const fetchMock = setup({
      'PATCH /api/v2/tasks/t1': makeTaskV2({ id: 't1', name: 'Vloer dweilen', roomId: 'r1', active: false }),
      'POST /api/v2/rooms/r1/tasks/bulk': { updated: 2 },
    });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderWithProviders(<TasksPage />);
    await expandAllRooms();

    fireEvent.click(await screen.findByRole('button', { name: 'Vloer dweilen deactiveren' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'PATCH', '/api/v2/tasks/t1')).toEqual({ active: false }));

    const bulk = screen.getByRole('group', { name: 'Acties voor alle taken in Keuken' });
    fireEvent.click(within(bulk).getByRole('button', { name: 'Alle taken deactiveren' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v2/rooms/r1/tasks/bulk')).toEqual({ op: 'deactivate' }));

    fireEvent.change(within(bulk).getByLabelText('Alle taken toewijzen aan'), { target: { value: BRAM._id } });
    fireEvent.click(within(bulk).getByRole('button', { name: 'Toewijzen' }));
    await waitFor(() => {
      const calls = fetchMock.mock.calls.filter(([u]) => u === '/api/v2/rooms/r1/tasks/bulk');
      expect(JSON.parse(String((calls.at(-1)![1] as RequestInit).body))).toEqual({
        op: 'reassign',
        defaultAssigneeId: BRAM._id,
      });
    });
  });

  it('permanently deletes a task after confirmation, with the version of the list item as If-Match', async () => {
    const fetchMock = setup({ 'DELETE /api/v2/tasks/t3': { deleted: true } });
    renderWithProviders(<TasksPage />);
    await expandAllRooms();
    fireEvent.click(await screen.findByRole('button', { name: 'Douche verwijderen' }));
    expect(screen.getByRole('heading', { name: 'Douche definitief verwijderen?' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Taak verwijderen' }));
    await waitFor(() => expect(headerOf(fetchMock, 'DELETE', '/api/v2/tasks/t3', 'if-match')).toBe('"5"'));
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Douche definitief verwijderen?' })).not.toBeInTheDocument());
  });
});

const STALE_MESSAGE = 'Deze gegevens zijn intussen door iemand anders gewijzigd. Controleer je wijziging en sla opnieuw op.';

function staleAnswer(version: number) {
  return () =>
    new Response(
      JSON.stringify({ type: 'urn:huishoudplanner:problem:precondition_failed', title: 'Precondition Failed', status: 412, detail: 'stale', traceId: 't' }),
      { status: 412, headers: { 'Content-Type': 'application/problem+json', ETag: `"${version}"` } },
    );
}

const sentWith = (fetchMock: ReturnType<typeof mockApi>, method: string, url: string) =>
  fetchMock.mock.calls
    .filter(([u, init]) => u === url && (init as RequestInit | undefined)?.method === method)
    .map(([, init]) => ({
      ifMatch: ((init as RequestInit).headers as Record<string, string>)['if-match'],
      body: (init as RequestInit).body ? JSON.parse(String((init as RequestInit).body)) : undefined,
    }));

const listReads = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.filter(([url]) => url === '/api/v2/tasks?limit=200').length;

describe('TasksPage — If-Match', () => {
  it('sends the version of the list item with a save', async () => {
    const fetchMock = setup({ 'PATCH /api/v2/tasks/t3': makeTaskV2({ id: 't3', name: 'Douche', roomId: 'r2', version: 6 }) });
    renderWithProviders(<TasksPage />);
    await expandAllRooms();
    fireEvent.click(await screen.findByRole('button', { name: 'Douche bewerken' }));
    const form = screen.getByRole('form', { name: 'Douche bewerken' });
    fireEvent.change(within(form).getByLabelText('Standaard uitvoerder'), { target: { value: BRAM._id } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(headerOf(fetchMock, 'PATCH', '/api/v2/tasks/t3', 'if-match')).toBe('"5"'));
  });

  it('sends the version of the list item with an activation and a deactivation', async () => {
    const fetchMock = setup({
      'PATCH /api/v2/tasks/t4': makeTaskV2({ id: 't4', name: 'Oude klus', roomId: 'r2', version: 2 }),
      'PATCH /api/v2/tasks/t1': makeTaskV2({ id: 't1', name: 'Vloer dweilen', roomId: 'r1', active: false, version: 3 }),
    });
    renderWithProviders(<TasksPage />);
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Toon inactieve taken' }));
    await expandAllRooms();

    fireEvent.click(await screen.findByRole('button', { name: 'Oude klus activeren' }));
    await waitFor(() => expect(headerOf(fetchMock, 'PATCH', '/api/v2/tasks/t4', 'if-match')).toBe('"1"'));
    fireEvent.click(await screen.findByRole('button', { name: 'Vloer dweilen deactiveren' }));
    await waitFor(() => expect(headerOf(fetchMock, 'PATCH', '/api/v2/tasks/t1', 'if-match')).toBe('"2"'));
  });

  it('uses the version of the answer for the next save, so a second save needs no re-read', async () => {
    let stored = tasks.map((task) => ({ ...task }));
    const fetchMock = setup({
      '/api/v2/tasks': () => page(stored),
      'PATCH /api/v2/tasks/t1': (init: RequestInit | undefined) => {
        const body = JSON.parse(String(init?.body)) as { active: boolean };
        stored = stored.map((task) => (task.id === 't1' ? { ...task, active: body.active, version: task.version + 1 } : task));
        return stored.find((task) => task.id === 't1');
      },
    });
    renderWithProviders(<TasksPage />);
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Toon inactieve taken' }));
    await expandAllRooms();

    fireEvent.click(await screen.findByRole('button', { name: 'Vloer dweilen deactiveren' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Vloer dweilen activeren' }));

    await waitFor(() => expect(sentWith(fetchMock, 'PATCH', '/api/v2/tasks/t1').map((call) => call.ifMatch)).toEqual(['"2"', '"3"']));
  });

  it('keeps the unsaved edit on a 412, reads the list again and saves with the new version once the person saves again', async () => {
    let list = tasks;
    let patches = 0;
    const fetchMock = setup({
      '/api/v2/tasks': () => page(list),
      'PATCH /api/v2/tasks/t3': () => {
        patches += 1;
        if (patches > 1) return makeTaskV2({ id: 't3', name: 'Douche (nieuw)', roomId: 'r2', defaultAssigneeId: BRAM._id, version: 7 });
        list = list.map((task) => (task.id === 't3' ? { ...task, name: 'Douche (nieuw)', version: 6 } : task));
        return staleAnswer(6)();
      },
    });
    renderWithProviders(<TasksPage />);
    await expandAllRooms();
    fireEvent.click(await screen.findByRole('button', { name: 'Douche bewerken' }));
    const form = screen.getByRole('form', { name: 'Douche bewerken' });
    fireEvent.change(within(form).getByLabelText('Standaard uitvoerder'), { target: { value: BRAM._id } });
    const readsBefore = listReads(fetchMock);
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));

    expect(await screen.findByText(STALE_MESSAGE)).toBeInTheDocument();
    await waitFor(() => expect(listReads(fetchMock)).toBeGreaterThan(readsBefore));
    // The edit is still there, in the same open form.
    const kept = await screen.findByRole('form', { name: /bewerken/ });
    expect(within(kept).getByLabelText('Standaard uitvoerder')).toHaveValue(BRAM._id);

    fireEvent.click(within(kept).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(patches).toBe(2));
    const sent = sentWith(fetchMock, 'PATCH', '/api/v2/tasks/t3');
    expect(sent.map((call) => call.ifMatch)).toEqual(['"5"', '"6"']);
    expect(sent[1]!.body).toMatchObject({ defaultAssigneeId: BRAM._id });
    await waitFor(() => expect(screen.queryByRole('form')).not.toBeInTheDocument());
    expect(screen.queryByText(STALE_MESSAGE)).not.toBeInTheDocument();
  });

  it('says so and reads the list again when a deactivation is stale', async () => {
    const fetchMock = setup({ 'PATCH /api/v2/tasks/t1': staleAnswer(9) });
    renderWithProviders(<TasksPage />);
    await expandAllRooms();
    const readsBefore = listReads(fetchMock);

    fireEvent.click(await screen.findByRole('button', { name: 'Vloer dweilen deactiveren' }));

    expect(await screen.findByText(STALE_MESSAGE)).toBeInTheDocument();
    await waitFor(() => expect(listReads(fetchMock)).toBeGreaterThan(readsBefore));
  });

  it('keeps the delete dialog open on a 412, with the message, and deletes with the new version when confirmed again', async () => {
    let list = tasks;
    let deletes = 0;
    const fetchMock = setup({
      '/api/v2/tasks': () => page(list),
      'DELETE /api/v2/tasks/t3': () => {
        deletes += 1;
        if (deletes > 1) return { deleted: true };
        list = list.map((task) => (task.id === 't3' ? { ...task, version: 8 } : task));
        return staleAnswer(8)();
      },
    });
    renderWithProviders(<TasksPage />);
    await expandAllRooms();
    fireEvent.click(await screen.findByRole('button', { name: 'Douche verwijderen' }));
    const readsBefore = listReads(fetchMock);
    fireEvent.click(screen.getByRole('button', { name: 'Taak verwijderen' }));

    expect(await screen.findByText(STALE_MESSAGE)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Douche definitief verwijderen?' })).toBeInTheDocument();
    await waitFor(() => expect(listReads(fetchMock)).toBeGreaterThan(readsBefore));

    fireEvent.click(screen.getByRole('button', { name: 'Taak verwijderen' }));
    await waitFor(() => expect(deletes).toBe(2));
    expect(sentWith(fetchMock, 'DELETE', '/api/v2/tasks/t3').map((call) => call.ifMatch)).toEqual(['"5"', '"8"']);
  });

  it('treats a 428 as a plain error, not as a stale edit', async () => {
    setup({ 'PATCH /api/v2/tasks/t1': () => problem(428, 'precondition_required') });
    renderWithProviders(<TasksPage />);
    await expandAllRooms();

    fireEvent.click(await screen.findByRole('button', { name: 'Vloer dweilen deactiveren' }));

    expect(await screen.findByText('Er ging iets mis.')).toBeInTheDocument();
    expect(screen.queryByText(STALE_MESSAGE)).not.toBeInTheDocument();
  });
});

describe('TasksPage — stale message placement and reset', () => {
  it('shows the stale-save message inside the edit dialog, where the overlay does not hide it', async () => {
    setup({ 'PATCH /api/v2/tasks/t3': staleAnswer(6) });
    renderWithProviders(<TasksPage />);
    await expandAllRooms();
    fireEvent.click(await screen.findByRole('button', { name: 'Douche bewerken' }));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(screen.getByRole('form', { name: 'Douche bewerken' })).getByRole('button', { name: 'Opslaan' }));

    const alert = await within(dialog).findByText(STALE_MESSAGE);
    expect(alert.closest('[role="alert"]')).not.toBeNull();
    expect(screen.getAllByText(STALE_MESSAGE)).toHaveLength(1);
  });

  it('does not show the stale delete error when the delete dialog opens for another task', async () => {
    setup({ 'DELETE /api/v2/tasks/t3': staleAnswer(8) });
    renderWithProviders(<TasksPage />);
    await expandAllRooms();
    fireEvent.click(await screen.findByRole('button', { name: 'Douche verwijderen' }));
    fireEvent.click(screen.getByRole('button', { name: 'Taak verwijderen' }));
    expect(await screen.findByText(STALE_MESSAGE)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Annuleren' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Aanrecht verwijderen' }));

    expect(screen.getByRole('heading', { name: 'Aanrecht definitief verwijderen?' })).toBeInTheDocument();
    expect(screen.queryByText(STALE_MESSAGE)).not.toBeInTheDocument();
  });

  it('says that the task no longer exists when it vanished during a stale delete', async () => {
    let list = tasks;
    setup({
      '/api/v2/tasks': () => page(list),
      'DELETE /api/v2/tasks/t3': () => {
        list = list.filter((task) => task.id !== 't3');
        return staleAnswer(8)();
      },
    });
    renderWithProviders(<TasksPage />);
    await expandAllRooms();
    fireEvent.click(await screen.findByRole('button', { name: 'Douche verwijderen' }));
    fireEvent.click(screen.getByRole('button', { name: 'Taak verwijderen' }));

    expect(await screen.findByText('Deze taak bestaat niet meer.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Douche definitief verwijderen?' })).not.toBeInTheDocument());
  });
});
