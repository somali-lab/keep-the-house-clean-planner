import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeRoom, makeSettings, makeTask, renderWithProviders } from '../../test/render.tsx';
import { TasksPage } from './TasksPage.tsx';

const keuken = makeRoom({ _id: 'r1', name: 'Keuken', sortOrder: 10 });
const badkamer = makeRoom({ _id: 'r2', name: 'Badkamer', sortOrder: 20 });

const tasks = [
  makeTask({ _id: 't1', name: 'Vloer dweilen', roomId: 'r1', intervalKey: '1w', durationMinutes: 20 }),
  makeTask({ _id: 't2', name: 'Aanrecht', roomId: 'r1', intervalKey: 'daily', durationMinutes: 5, defaultAssigneeId: ANNA._id }),
  makeTask({ _id: 't3', name: 'Douche', roomId: 'r2', intervalKey: '2wk', durationMinutes: 30 }),
  makeTask({ _id: 't4', name: 'Oude klus', roomId: 'r2', active: false }),
];

function setup(extraRoutes: Record<string, unknown> = {}) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/rooms': [badkamer, keuken],
    '/api/tasks': tasks,
    '/api/settings': makeSettings(),
    ...extraRoutes,
  });
}

function bodyOf(fetchMock: ReturnType<typeof mockApi>, method: string, url: string): unknown {
  const call = fetchMock.mock.calls.find(([u, init]) => u === url && (init as RequestInit | undefined)?.method === method);
  return call ? JSON.parse(String((call[1] as RequestInit).body)) : undefined;
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
    const sections = await screen.findAllByRole('region');
    expect(sections.map((s) => within(s).getByRole('heading', { level: 2 }).textContent)).toEqual(['Keuken', 'Badkamer']);

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
    const row = (await screen.findByText('Oude klus')).closest('li')!;
    expect(row).toHaveTextContent('Inactief');
    expect(within(row).getByRole('button', { name: 'Oude klus activeren' })).toBeInTheDocument();
  });

  it('links each task to its history', async () => {
    renderWithProviders(<TasksPage />);
    const row = (await screen.findByText('Douche')).closest('li')!;
    expect(within(row).getByRole('link', { name: 'Geschiedenis' })).toHaveAttribute(
      'href',
      '/history?entity=task&entityId=t3',
    );
  });

  it('filters the list by room', async () => {
    renderWithProviders(<TasksPage />);
    fireEvent.change(await screen.findByLabelText('Filter op ruimte'), { target: { value: 'r2' } });
    const sections = screen.getAllByRole('region');
    expect(sections).toHaveLength(1);
    expect(within(sections[0]!).getByRole('heading', { level: 2 })).toHaveTextContent('Badkamer');
    expect(screen.queryByText('Aanrecht')).not.toBeInTheDocument();
  });
});

describe('TasksPage — form validation', () => {
  it('requires a duration and does not call the API without one', async () => {
    const fetchMock = setup({ 'POST /api/tasks': makeTask({ _id: 't9', name: 'Ramen', roomId: 'r1' }) });
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
    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/tasks')).toEqual({
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
    const fetchMock = setup({ 'PATCH /api/tasks/t3': makeTask({ _id: 't3', name: 'Douche', roomId: 'r2' }) });
    renderWithProviders(<TasksPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Taak toevoegen aan Badkamer' }));
    expect(within(screen.getByRole('form')).getByLabelText('Ruimte')).toHaveValue('r2');
    fireEvent.click(within(screen.getByRole('form')).getByRole('button', { name: 'Annuleren' }));

    fireEvent.click(screen.getByRole('button', { name: 'Douche bewerken' }));
    const form = screen.getByRole('form', { name: 'Douche bewerken' });
    expect(within(form).getByLabelText('Duur (minuten)')).toHaveValue(30);
    fireEvent.change(within(form).getByLabelText('Standaard uitvoerder'), { target: { value: BRAM._id } });
    fireEvent.click(within(form).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() =>
      expect(bodyOf(fetchMock, 'PATCH', '/api/tasks/t3')).toMatchObject({ durationMinutes: 30, defaultAssigneeId: BRAM._id }),
    );
  });
});

describe('TasksPage — actions', () => {
  it('deactivates a single task and runs bulk actions per room', async () => {
    const fetchMock = setup({
      'PATCH /api/tasks/t1': makeTask({ _id: 't1', name: 'Vloer dweilen', roomId: 'r1', active: false }),
      'POST /api/rooms/r1/tasks/bulk': { updated: 2 },
    });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderWithProviders(<TasksPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Vloer dweilen deactiveren' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'PATCH', '/api/tasks/t1')).toEqual({ active: false }));

    const bulk = screen.getByRole('group', { name: 'Acties voor alle taken in Keuken' });
    fireEvent.click(within(bulk).getByRole('button', { name: 'Alle taken deactiveren' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/rooms/r1/tasks/bulk')).toEqual({ op: 'deactivate' }));

    fireEvent.change(within(bulk).getByLabelText('Alle taken toewijzen aan'), { target: { value: BRAM._id } });
    fireEvent.click(within(bulk).getByRole('button', { name: 'Toewijzen' }));
    await waitFor(() => {
      const calls = fetchMock.mock.calls.filter(([u]) => u === '/api/rooms/r1/tasks/bulk');
      expect(JSON.parse(String((calls.at(-1)![1] as RequestInit).body))).toEqual({
        op: 'reassign',
        defaultAssigneeId: BRAM._id,
      });
    });
  });

  it('permanently deletes a task after confirmation', async () => {
    const fetchMock = setup({ 'DELETE /api/tasks/t3': { deleted: true } });
    renderWithProviders(<TasksPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Douche verwijderen' }));
    expect(screen.getByRole('heading', { name: 'Douche definitief verwijderen?' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Taak verwijderen' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => url === '/api/tasks/t3' && (init as RequestInit)?.method === 'DELETE')).toBe(true));
  });
});
