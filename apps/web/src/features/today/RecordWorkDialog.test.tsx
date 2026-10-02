import type { OccurrenceView } from '@huishoudplanner/shared';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeOccurrence, makeRoom, makeTask, renderWithProviders } from '../../test/render.tsx';
import { RecordWorkDialog } from './RecordWorkDialog.tsx';

const TODAY = '2026-09-16';
const KEY = /^[A-Za-z0-9_-]{16,64}$/;

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

function setup(routes: Record<string, unknown> = {}, props: { initialTaskId?: string } = {}) {
  storeProfile(ANNA._id);
  const fetchMock = mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/rooms': [makeRoom({ _id: 'r1', name: 'Keuken' }), makeRoom({ _id: 'r2', name: 'Zolder', active: false })],
    '/api/tasks': [
      makeTask({ _id: 't1', name: 'Stofzuigen', roomId: 'r1' }),
      makeTask({ _id: 't2', name: 'Afwas', roomId: 'r1' }),
      makeTask({ _id: 't3', name: 'Oud', roomId: 'r1', active: false }),
    ],
    ...routes,
  });
  const onOpenChange = vi.fn();
  const onRecorded = vi.fn();
  renderWithProviders(
    <RecordWorkDialog open onOpenChange={onOpenChange} todayKey={TODAY} onRecorded={onRecorded} {...props} />,
  );
  return { fetchMock, onOpenChange, onRecorded };
}

const posts = (fetchMock: ReturnType<typeof mockApi>, url: string) =>
  fetchMock.mock.calls
    .filter(([u, init]) => u === url && (init as RequestInit | undefined)?.method === 'POST')
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)) as Record<string, unknown>);

const anyPost = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'POST');

const dialog = () => screen.getByRole('dialog', { name: 'Gedaan werk vastleggen' });
const submit = () => within(dialog()).getByRole('button', { name: 'Vastleggen' });
const choose = (name: string) => fireEvent.click(within(dialog()).getByRole('radio', { name }));
const oneOffChoice = 'Eenmalige taak (komt niet in de takenlijst)';

describe('RecordWorkDialog', () => {
  it('offers the two choices as labelled radios, with the extra execution first', async () => {
    setup();
    const extra = await screen.findByRole('radio', { name: 'Extra keer voor een bestaande taak' });
    const oneOff = screen.getByRole('radio', { name: oneOffChoice });
    expect(extra).toBeChecked();
    expect(oneOff).not.toBeChecked();
    expect(extra).toHaveAccessibleDescription(/telt mee voor de achterstand/);
    expect(oneOff).toHaveAccessibleDescription(/alleen in geschiedenis en statistiek/);

    // Each choice shows only its own fields.
    expect(await screen.findByLabelText('Taak')).toBeInTheDocument();
    expect(screen.queryByLabelText('Naam van de klus')).not.toBeInTheDocument();
    choose(oneOffChoice);
    expect(screen.getByLabelText('Naam van de klus')).toBeInTheDocument();
    expect(screen.getByLabelText('Ruimte (optioneel)')).toBeInTheDocument();
    expect(screen.getByLabelText('Duur (minuten)')).toBeInTheDocument();
    expect(screen.queryByLabelText('Taak')).not.toBeInTheDocument();
    expect(screen.getByText('Wordt vastgelegd als gedaan op wo 16-09.')).toBeInTheDocument();
  });

  it('records an extra execution of an active task as done today by the chosen person', async () => {
    const { fetchMock, onRecorded, onOpenChange } = setup({
      'POST /api/occurrences': makeOccurrence({ _id: 'new1', taskId: 't1', origin: 'adhoc', recordedDone: true }),
    });
    const task = await screen.findByLabelText('Taak');
    // Inactive tasks are not offered, and the room helps to tell tasks apart.
    await waitFor(() => expect(within(task).getAllByRole('option')).toHaveLength(3));
    expect(within(task).getAllByRole('option').map((o) => o.textContent)).toEqual(['Kies een taak', 'Afwas · Keuken', 'Stofzuigen · Keuken']);
    expect(screen.getByLabelText('Gedaan door')).toHaveValue(ANNA._id);

    fireEvent.change(task, { target: { value: 't1' } });
    fireEvent.change(screen.getByLabelText('Gedaan door'), { target: { value: BRAM._id } });
    fireEvent.click(submit());

    await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
    expect(posts(fetchMock, '/api/occurrences')).toEqual([
      { taskId: 't1', date: TODAY, assigneeId: BRAM._id, done: true, requestId: expect.stringMatching(KEY) },
    ]);
    expect(onRecorded.mock.calls[0]![0]).toMatchObject({ _id: 'new1' });
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('starts on the task it was opened for', async () => {
    setup({}, { initialTaskId: 't2' });
    const task = await screen.findByLabelText('Taak');
    await waitFor(() => expect(task).toHaveValue('t2'));
  });

  it('records a one-off task with its name, optional room and duration', async () => {
    const { fetchMock, onRecorded } = setup({
      'POST /api/occurrences/one-off': makeOccurrence({ _id: 'new2', taskId: null, origin: 'adhoc', recordedDone: true }),
    });
    await screen.findByLabelText('Taak');
    choose(oneOffChoice);
    const room = screen.getByLabelText('Ruimte (optioneel)');
    // Only active rooms, and "no room" is the default.
    await waitFor(() => expect(within(room).getAllByRole('option')).toHaveLength(2));
    expect(within(room).getAllByRole('option').map((o) => o.textContent)).toEqual(['Zonder ruimte', 'Keuken']);
    fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: '  Gordijnen ophangen ' } });
    fireEvent.change(room, { target: { value: 'r1' } });
    fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '40' } });
    fireEvent.click(submit());

    await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
    expect(posts(fetchMock, '/api/occurrences/one-off')).toEqual([
      {
        name: 'Gordijnen ophangen',
        roomId: 'r1',
        durationMinutes: 40,
        date: TODAY,
        assigneeId: ANNA._id,
        done: true,
        requestId: expect.stringMatching(KEY),
      },
    ]);
    expect(posts(fetchMock, '/api/occurrences')).toEqual([]);
  });

  it('sends a one-off task without a room as roomId null', async () => {
    const { fetchMock, onRecorded } = setup({
      'POST /api/occurrences/one-off': makeOccurrence({ _id: 'new2', taskId: null, origin: 'adhoc', recordedDone: true }),
    });
    await screen.findByLabelText('Taak');
    choose(oneOffChoice);
    await waitFor(() => expect(screen.getByLabelText('Gedaan door')).toHaveValue(ANNA._id));
    fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: 'Kast ophalen' } });
    fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '15' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRecorded).toHaveBeenCalled());
    expect(posts(fetchMock, '/api/occurrences/one-off')[0]).toMatchObject({ roomId: null });
  });

  it('a double click creates one record: the second click is ignored while the first is pending', async () => {
    const reply = deferred<OccurrenceView>();
    const { fetchMock, onRecorded } = setup({ 'POST /api/occurrences': () => reply.promise });
    const task = await screen.findByLabelText('Taak');
    await waitFor(() => expect(within(task).getAllByRole('option')).toHaveLength(3));
    fireEvent.change(task, { target: { value: 't2' } });

    const button = submit();
    fireEvent.click(button);
    fireEvent.click(button);
    expect(await screen.findByRole('button', { name: 'Bezig met vastleggen…' })).toBeDisabled();
    expect(posts(fetchMock, '/api/occurrences')).toHaveLength(1);

    reply.resolve(makeOccurrence({ _id: 'new1', taskId: 't2', origin: 'adhoc', recordedDone: true }));
    await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
    expect(posts(fetchMock, '/api/occurrences')).toHaveLength(1);
  });

  it('reuses the request key when the same values are retried after a failure, and takes a new one when they change', async () => {
    let attempts = 0;
    const { fetchMock, onRecorded } = setup({
      'POST /api/occurrences': () => {
        attempts += 1;
        if (attempts <= 2) throw new TypeError('network down');
        return makeOccurrence({ _id: 'new1', taskId: 't1', origin: 'adhoc', recordedDone: true });
      },
    });
    const task = await screen.findByLabelText('Taak');
    await waitFor(() => expect(within(task).getAllByRole('option')).toHaveLength(3));
    fireEvent.change(task, { target: { value: 't1' } });
    fireEvent.click(submit());
    expect(await screen.findByText('Vastleggen is niet gelukt. Probeer het opnieuw.')).toBeInTheDocument();
    await waitFor(() => expect(submit()).toBeEnabled());
    fireEvent.click(submit());
    await waitFor(() => expect(posts(fetchMock, '/api/occurrences')).toHaveLength(2));

    await waitFor(() => expect(submit()).toBeEnabled());
    fireEvent.change(screen.getByLabelText('Taak'), { target: { value: 't2' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));

    const [first, second, third] = posts(fetchMock, '/api/occurrences') as { requestId: string; taskId: string }[];
    expect(second!.requestId).toBe(first!.requestId);
    expect(third!.taskId).toBe('t2');
    expect(third!.requestId).not.toBe(first!.requestId);
  });

  it('refuses an incomplete form with a message per field and sends nothing', async () => {
    const { fetchMock } = setup();
    await screen.findByLabelText('Taak');
    fireEvent.click(submit());
    expect(await screen.findByText('Kies een taak.')).toBeInTheDocument();
    expect(screen.getByLabelText('Taak')).toBeInvalid();

    choose(oneOffChoice);
    fireEvent.click(submit());
    expect(await screen.findByText('Vul een naam in.')).toBeInTheDocument();
    expect(screen.getByText('Vul de duur in hele minuten in (minstens 1).')).toBeInTheDocument();
    expect(screen.getByLabelText('Naam van de klus')).toBeInvalid();
    expect(screen.getByLabelText('Duur (minuten)')).toBeInvalid();

    fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: '   ' } });
    fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '0' } });
    fireEvent.click(submit());
    expect(screen.getByText('Vul een naam in.')).toBeInTheDocument();
    expect(screen.getByText('Vul de duur in hele minuten in (minstens 1).')).toBeInTheDocument();
    expect(anyPost(fetchMock)).toEqual([]);
  });

  it('closes on cancel without sending anything', async () => {
    const { fetchMock, onOpenChange } = setup();
    await screen.findByLabelText('Taak');
    fireEvent.click(within(dialog()).getByRole('button', { name: 'Annuleren' }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
    expect(anyPost(fetchMock)).toEqual([]);
  });
});
