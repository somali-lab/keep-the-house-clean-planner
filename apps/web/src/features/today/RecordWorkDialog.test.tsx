import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Occurrence } from '../../api/index.ts';
import { resetRequestKeys } from '../../api/requestKey.ts';
import { ANNA, BRAM, describeRequest, LIMITS, mockApi, page, problem, storeProfile, v2Basics } from '../../test/fixtures.ts';
import { makeOccurrenceV2, makeRoom, makeTask, renderWithProviders } from '../../test/render.tsx';
import { RecordWorkDialog } from './RecordWorkDialog.tsx';

const TODAY = '2026-09-16';

afterEach(() => resetRequestKeys());
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
    ...v2Basics(),
    '/api/v2/occurrences': page([]),
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
  const open = () =>
    renderWithProviders(
      <RecordWorkDialog open onOpenChange={onOpenChange} todayKey={TODAY} onRecorded={onRecorded} {...props} />,
    );
  const first = open();
  // Closing the dialog unmounts it; reopening starts a fresh form.
  const reopen = () => {
    first.unmount();
    return open();
  };
  return { fetchMock, onOpenChange, onRecorded, reopen };
}

const posts = (fetchMock: ReturnType<typeof mockApi>, url: string) =>
  fetchMock.mock.calls
    .filter(([u, init]) => u === url && (init as RequestInit | undefined)?.method === 'POST')
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)) as Record<string, unknown>);

const anyPost = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'POST');

const dialog = () => screen.getByRole('dialog', { name: 'Extra taak' });
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
      'POST /api/v2/occurrences': makeOccurrenceV2({ id: 'new1', taskId: 't1', origin: 'adhoc', recordedDone: true }),
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
    expect(posts(fetchMock, '/api/v2/occurrences')).toEqual([
      { taskId: 't1', date: TODAY, assigneeId: BRAM._id, done: true, requestId: expect.stringMatching(KEY) },
    ]);
    expect(onRecorded.mock.calls[0]![0]).toMatchObject({ id: 'new1' });
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('starts on the task it was opened for', async () => {
    setup({}, { initialTaskId: 't2' });
    const task = await screen.findByLabelText('Taak');
    await waitFor(() => expect(task).toHaveValue('t2'));
  });

  it('records a one-off task with its name, optional room and duration', async () => {
    const { fetchMock, onRecorded } = setup({
      'POST /api/v2/occurrences/one-off': makeOccurrenceV2({ id: 'new2', taskId: null, origin: 'adhoc', recordedDone: true }),
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
    expect(posts(fetchMock, '/api/v2/occurrences/one-off')).toEqual([
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
    expect(posts(fetchMock, '/api/v2/occurrences')).toEqual([]);
  });

  it('leaves the default points to the server: the field starts empty and says what the server accepts', async () => {
    const { fetchMock, onRecorded } = setup({
      'POST /api/v2/occurrences/one-off': makeOccurrenceV2({ id: 'new2', taskId: null, origin: 'adhoc', recordedDone: true }),
    });
    await screen.findByLabelText('Taak');
    expect(screen.queryByLabelText('Punten')).not.toBeInTheDocument();
    choose(oneOffChoice);
    await waitFor(() => expect(screen.getByLabelText('Gedaan door')).toHaveValue(ANNA._id));
    const points = screen.getByLabelText('Punten');
    // The range comes from GET /api/v2/meta/limits.
    await waitFor(() => expect(points).toHaveAccessibleDescription(/Van 0 tot 1000. Standaard één punt per minuut/));
    expect(points).toHaveAttribute('min', '0');
    expect(points).toHaveAttribute('max', '1000');
    expect(points).toHaveValue(null);
    fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '40' } });
    expect(points).toHaveValue(null);

    fireEvent.change(points, { target: { value: '8' } });
    fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '90' } });
    expect(points).toHaveValue(8);
    fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: 'Kast ophalen' } });
    fireEvent.click(submit());

    await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
    expect(posts(fetchMock, '/api/v2/occurrences/one-off')).toEqual([
      expect.objectContaining({ name: 'Kast ophalen', durationMinutes: 90, points: 8, done: true }),
    ]);
  });

  it('reads the limits once for the session', async () => {
    const { fetchMock } = setup();
    await screen.findByLabelText('Taak');
    choose(oneOffChoice);
    await screen.findByLabelText('Punten');
    expect(fetchMock.mock.calls.filter(([url]) => url === '/api/v2/meta/limits')).toHaveLength(1);
  });

  it('uses the range of the server in the points message', async () => {
    setup({ '/api/v2/meta/limits': { ...LIMITS, tasks: { ...LIMITS.tasks, maxPoints: 500 } } });
    await screen.findByLabelText('Taak');
    choose(oneOffChoice);
    await waitFor(() => expect(screen.getByLabelText('Gedaan door')).toHaveValue(ANNA._id));
    fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: 'Kast ophalen' } });
    fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '15' } });
    await waitFor(() => expect(screen.getByLabelText('Punten')).toHaveAttribute('max', '500'));
    fireEvent.change(screen.getByLabelText('Punten'), { target: { value: '501' } });
    fireEvent.click(submit());
    expect(await screen.findByText('Vul de punten in als heel getal van 0 tot 500.')).toBeInTheDocument();
  });

  it('leaves the points out when they were not edited, so the server applies the default', async () => {
    const { fetchMock, onRecorded } = setup({
      'POST /api/v2/occurrences/one-off': makeOccurrenceV2({ id: 'new2', taskId: null, origin: 'adhoc', recordedDone: true }),
    });
    await screen.findByLabelText('Taak');
    choose(oneOffChoice);
    await waitFor(() => expect(screen.getByLabelText('Gedaan door')).toHaveValue(ANNA._id));
    fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: 'Kast ophalen' } });
    fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '15' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
    expect(posts(fetchMock, '/api/v2/occurrences/one-off')[0]).not.toHaveProperty('points');
  });

  it('sends 0 points and rejects points above 1000 without a request', async () => {
    const { fetchMock, onRecorded } = setup({
      'POST /api/v2/occurrences/one-off': makeOccurrenceV2({ id: 'new2', taskId: null, origin: 'adhoc', recordedDone: true }),
    });
    await screen.findByLabelText('Taak');
    choose(oneOffChoice);
    await waitFor(() => expect(screen.getByLabelText('Gedaan door')).toHaveValue(ANNA._id));
    fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: 'Kast ophalen' } });
    fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '15' } });
    const points = screen.getByLabelText('Punten');

    fireEvent.change(points, { target: { value: '1001' } });
    fireEvent.click(submit());
    expect(await screen.findByText('Vul de punten in als heel getal van 0 tot 1000.')).toBeInTheDocument();
    expect(points).toHaveAttribute('aria-invalid', 'true');
    expect(anyPost(fetchMock)).toEqual([]);

    fireEvent.change(points, { target: { value: '0' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
    expect(posts(fetchMock, '/api/v2/occurrences/one-off')).toEqual([expect.objectContaining({ points: 0 })]);
  });

  it('sends a one-off task without a room as roomId null', async () => {
    const { fetchMock, onRecorded } = setup({
      'POST /api/v2/occurrences/one-off': makeOccurrenceV2({ id: 'new2', taskId: null, origin: 'adhoc', recordedDone: true }),
    });
    await screen.findByLabelText('Taak');
    choose(oneOffChoice);
    await waitFor(() => expect(screen.getByLabelText('Gedaan door')).toHaveValue(ANNA._id));
    fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: 'Kast ophalen' } });
    fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '15' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRecorded).toHaveBeenCalled());
    expect(posts(fetchMock, '/api/v2/occurrences/one-off')[0]).toMatchObject({ roomId: null });
  });

  it('a double click creates one record: the second click is ignored while the first is pending', async () => {
    const reply = deferred<Occurrence>();
    const { fetchMock, onRecorded } = setup({ 'POST /api/v2/occurrences': () => reply.promise });
    const task = await screen.findByLabelText('Taak');
    await waitFor(() => expect(within(task).getAllByRole('option')).toHaveLength(3));
    fireEvent.change(task, { target: { value: 't2' } });

    const button = submit();
    fireEvent.click(button);
    fireEvent.click(button);
    expect(await screen.findByRole('button', { name: 'Bezig met vastleggen…' })).toBeDisabled();
    expect(posts(fetchMock, '/api/v2/occurrences')).toHaveLength(1);

    reply.resolve(makeOccurrenceV2({ id: 'new1', taskId: 't2', origin: 'adhoc', recordedDone: true }));
    await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
    expect(posts(fetchMock, '/api/v2/occurrences')).toHaveLength(1);
  });

  it('reuses the request key when the same values are retried after a failure, and takes a new one when they change', async () => {
    let attempts = 0;
    const { fetchMock, onRecorded } = setup({
      'POST /api/v2/occurrences': () => {
        attempts += 1;
        if (attempts <= 2) throw new TypeError('network down');
        return makeOccurrenceV2({ id: 'new1', taskId: 't1', origin: 'adhoc', recordedDone: true });
      },
    });
    const task = await screen.findByLabelText('Taak');
    await waitFor(() => expect(within(task).getAllByRole('option')).toHaveLength(3));
    fireEvent.change(task, { target: { value: 't1' } });
    fireEvent.click(submit());
    expect(await screen.findByText('Vastleggen is niet gelukt. Probeer het opnieuw.')).toBeInTheDocument();
    await waitFor(() => expect(submit()).toBeEnabled());
    fireEvent.click(submit());
    await waitFor(() => expect(posts(fetchMock, '/api/v2/occurrences')).toHaveLength(2));

    await waitFor(() => expect(submit()).toBeEnabled());
    fireEvent.change(screen.getByLabelText('Taak'), { target: { value: 't2' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));

    const [first, second, third] = posts(fetchMock, '/api/v2/occurrences') as { requestId: string; taskId: string }[];
    expect(second!.requestId).toBe(first!.requestId);
    expect(third!.taskId).toBe('t2');
    expect(third!.requestId).not.toBe(first!.requestId);
  });

  it('keeps the request key of a failed request when the dialog is closed and reopened with the same values', async () => {
    let attempts = 0;
    const { fetchMock, reopen, onRecorded } = setup({
      'POST /api/v2/occurrences': () => {
        attempts += 1;
        if (attempts === 1) throw new TypeError('network down');
        return makeOccurrenceV2({ id: 'new1', taskId: 't1', origin: 'adhoc', recordedDone: true });
      },
    });
    const fill = async () => {
      const task = await screen.findByLabelText('Taak', { selector: 'select' });
      await waitFor(() => expect(within(task).getAllByRole('option')).toHaveLength(3));
      await waitFor(() => expect(screen.getByLabelText('Gedaan door')).toHaveValue(ANNA._id));
      fireEvent.change(task, { target: { value: 't1' } });
    };
    await fill();
    fireEvent.click(submit());
    expect(await screen.findByText('Vastleggen is niet gelukt. Probeer het opnieuw.')).toBeInTheDocument();

    reopen();
    await fill();
    fireEvent.click(submit());
    await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
    const [first, second] = posts(fetchMock, '/api/v2/occurrences') as { requestId: string }[];
    expect(second!.requestId).toBe(first!.requestId);
  });

  it('refuses an incomplete form with one alert, a message per field and focus on the first invalid field', async () => {
    const { fetchMock } = setup();
    await screen.findByLabelText('Taak', { selector: 'select' });
    await waitFor(() => expect(submit()).toBeEnabled());
    fireEvent.click(submit());
    const task = screen.getByLabelText('Taak', { selector: 'select' });
    expect(await screen.findByText('Kies een taak.')).toBeInTheDocument();
    expect(task).toBeInvalid();
    expect(task).toHaveAccessibleDescription('Kies een taak.');
    expect(task).toHaveFocus();
    expect(screen.getAllByRole('alert')).toHaveLength(1);
    expect(screen.getByRole('alert')).toHaveTextContent('Controleer de gemarkeerde velden');

    choose(oneOffChoice);
    fireEvent.click(submit());
    expect(await screen.findByText('Vul een naam in.')).toBeInTheDocument();
    expect(screen.getByText('Vul de duur in hele minuten in (minstens 1).')).toBeInTheDocument();
    expect(screen.getByLabelText('Naam van de klus')).toBeInvalid();
    expect(screen.getByLabelText('Naam van de klus')).toHaveFocus();
    expect(screen.getByLabelText('Duur (minuten)')).toBeInvalid();
    expect(screen.getAllByRole('alert')).toHaveLength(1);

    fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: '   ' } });
    fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '0' } });
    fireEvent.click(submit());
    expect(screen.getByText('Vul een naam in.')).toBeInTheDocument();
    expect(screen.getByText('Vul de duur in hele minuten in (minstens 1).')).toBeInTheDocument();
    expect(anyPost(fetchMock)).toEqual([]);
  });

  it('cannot be submitted before the profile has loaded', async () => {
    setup();
    expect(submit()).toBeDisabled();
    await waitFor(() => expect(submit()).toBeEnabled());
  });

  describe('when the chosen task is still planned today', () => {
    const PLANNED = makeOccurrenceV2({ id: 'o-planned', taskId: 't1', taskNameSnapshot: 'Stofzuigen', date: TODAY, assigneeId: ANNA._id });

    async function chooseStofzuigen() {
      const task = await screen.findByLabelText('Taak', { selector: 'select' });
      await waitFor(() => expect(within(task).getAllByRole('option')).toHaveLength(3));
      await waitFor(() => expect(screen.getByLabelText('Gedaan door')).toHaveValue(ANNA._id));
      fireEvent.change(task, { target: { value: 't1' } });
    }

    it('says so and checks off the planned occurrence by default, for the chosen person', async () => {
      const { fetchMock, onRecorded } = setup({
        '/api/v2/occurrences': page([PLANNED, makeOccurrenceV2({ id: 'o-other', taskId: 't2', date: TODAY }), makeOccurrenceV2({ id: 'o-tomorrow', taskId: 't2', date: '2026-09-17' })]),
        'POST /api/v2/occurrences/o-planned/complete': { ...PLANNED, status: 'done', completedBy: BRAM._id },
      });
      await chooseStofzuigen();
      const choices = await screen.findByRole('group', { name: '"Stofzuigen" staat vandaag nog open in het plan.' });
      expect(within(choices).getByRole('radio', { name: 'Vink de geplande taak af' })).toBeChecked();
      expect(within(choices).getByRole('radio', { name: 'Toch een extra keer registreren' })).not.toBeChecked();

      fireEvent.change(screen.getByLabelText('Gedaan door'), { target: { value: BRAM._id } });
      fireEvent.click(within(dialog()).getByRole('button', { name: 'Afvinken' }));
      await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
      expect(onRecorded.mock.calls[0]![1]).toBe('checkedOff');
      const completions = fetchMock.mock.calls.filter(([u, init]) => u === '/api/v2/occurrences/o-planned/complete' && init?.method === 'POST');
      expect(completions.map(([, init]) => JSON.parse(String(init!.body)))).toEqual([{ completedBy: BRAM._id }]);
      expect(posts(fetchMock, '/api/v2/occurrences')).toEqual([]);
    });

    it('records an extra execution anyway when that is chosen, and leaves the planned task open', async () => {
      const { fetchMock, onRecorded } = setup({
        '/api/v2/occurrences': page([PLANNED]),
        'POST /api/v2/occurrences': makeOccurrenceV2({ id: 'new1', taskId: 't1', origin: 'adhoc', recordedDone: true }),
      });
      await chooseStofzuigen();
      fireEvent.click(await screen.findByRole('radio', { name: 'Toch een extra keer registreren' }));
      expect(within(dialog()).queryByRole('button', { name: 'Afvinken' })).not.toBeInTheDocument();
      fireEvent.click(submit());
      await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
      expect(onRecorded.mock.calls[0]![1]).toBe('recorded');
      expect(posts(fetchMock, '/api/v2/occurrences')).toHaveLength(1);
      expect(fetchMock.mock.calls.filter(([u]) => /\/complete$/.test(String(u)))).toEqual([]);
    });

    it('does not mention it for a task that is not planned today or only planned later', async () => {
      setup({ '/api/v2/occurrences': page([{ ...PLANNED, date: '2026-09-17' }, { ...PLANNED, id: 'o-done', status: 'done' }]) });
      await chooseStofzuigen();
      expect(screen.queryByRole('group', { name: /staat vandaag nog open/ })).not.toBeInTheDocument();
      expect(submit()).toBeInTheDocument();
    });
  });

  describe('planning instead of recording', () => {
    const planMode = () => choose('Inplannen');
    const planSubmit = () => within(dialog()).getByRole('button', { name: 'Inplannen' });
    const dateField = () => screen.getByLabelText('Datum');
    async function chooseTask(id: string) {
      const task = await screen.findByLabelText('Taak', { selector: 'select' });
      await waitFor(() => expect(within(task).getAllByRole('option')).toHaveLength(3));
      fireEvent.change(task, { target: { value: id } });
    }
    /** Lets the server answer the planning request with an error status. */
    function refusePlanning(fetchMock: ReturnType<typeof mockApi>, code: string, status: number) {
      vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
        const described = await describeRequest(input, init);
        return described.url === '/api/v2/occurrences' && described.init?.method === 'POST'
          ? problem(status, code, 'refused')
          : fetchMock(described.url, described.init);
      });
    }

    it('offers the mode as a radio group with a legend, defaults to already done, and swaps the person and date fields', async () => {
      setup();
      const group = await screen.findByRole('group', { name: 'Wanneer?' });
      const done = within(group).getByRole('radio', { name: 'Al gedaan (vandaag)' });
      const plan = within(group).getByRole('radio', { name: 'Inplannen' });
      expect(done).toBeChecked();
      expect(plan).not.toBeChecked();
      expect(plan).toHaveAccessibleDescription('Komt als open taak op de gekozen dag.');
      expect(screen.getByLabelText('Gedaan door')).toBeInTheDocument();
      expect(screen.queryByLabelText('Datum')).not.toBeInTheDocument();

      planMode();
      expect(plan).toBeChecked();
      expect(screen.queryByLabelText('Gedaan door')).not.toBeInTheDocument();
      expect(screen.queryByText(/Wordt vastgelegd als gedaan/)).not.toBeInTheDocument();
      // Today or later, nothing earlier; the person defaults to anyone and lists the active users.
      expect(dateField()).toHaveValue(TODAY);
      expect(dateField()).toHaveAttribute('min', TODAY);
      const who = screen.getByLabelText('Voor wie');
      expect(who).toHaveValue('');
      await waitFor(() => expect(within(who).getAllByRole('option')).toHaveLength(3));
      expect(within(who).getAllByRole('option').map((o) => o.textContent)).toEqual(['Wie dan ook', ANNA.name, BRAM.name]);
      expect(planSubmit()).toHaveAttribute('type', 'submit');

      choose('Al gedaan (vandaag)');
      expect(screen.getByLabelText('Gedaan door')).toBeInTheDocument();
      expect(within(dialog()).getByRole('button', { name: 'Vastleggen' })).toBeInTheDocument();
    });

    it('plans an extra execution for another person on a later day as an open occurrence', async () => {
      const { fetchMock, onRecorded, onOpenChange } = setup({
        'POST /api/v2/occurrences': makeOccurrenceV2({ id: 'new1', taskId: 't1', origin: 'adhoc', date: '2026-09-18', assigneeId: BRAM._id }),
      });
      await chooseTask('t1');
      planMode();
      fireEvent.change(dateField(), { target: { value: '2026-09-18' } });
      fireEvent.change(screen.getByLabelText('Voor wie'), { target: { value: BRAM._id } });
      fireEvent.click(planSubmit());

      await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
      expect(posts(fetchMock, '/api/v2/occurrences')).toEqual([
        { taskId: 't1', date: '2026-09-18', assigneeId: BRAM._id, requestId: expect.stringMatching(KEY) },
      ]);
      expect(onRecorded.mock.calls[0]![1]).toBe('planned');
      expect(onOpenChange).toHaveBeenCalledWith(false);
    });

    it('plans a one-off task for anyone', async () => {
      const { fetchMock, onRecorded } = setup({
        'POST /api/v2/occurrences/one-off': makeOccurrenceV2({ id: 'new2', taskId: null, origin: 'adhoc', date: '2026-09-20', assigneeId: null }),
      });
      await screen.findByLabelText('Taak');
      choose(oneOffChoice);
      planMode();
      fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: 'Zolder opruimen' } });
      await waitFor(() => expect(within(screen.getByLabelText('Ruimte (optioneel)')).getAllByRole('option')).toHaveLength(2));
      fireEvent.change(screen.getByLabelText('Ruimte (optioneel)'), { target: { value: 'r1' } });
      fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '90' } });
      fireEvent.change(dateField(), { target: { value: '2026-09-20' } });
      fireEvent.click(planSubmit());

      await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
      expect(posts(fetchMock, '/api/v2/occurrences/one-off')).toEqual([
        { name: 'Zolder opruimen', roomId: 'r1', durationMinutes: 90, date: '2026-09-20', assigneeId: null, requestId: expect.stringMatching(KEY) },
      ]);
      expect(posts(fetchMock, '/api/v2/occurrences')).toEqual([]);
    });

    it('plans a one-off task with the chosen points', async () => {
      const { fetchMock, onRecorded } = setup({
        'POST /api/v2/occurrences/one-off': makeOccurrenceV2({ id: 'new2', taskId: null, origin: 'adhoc', date: '2026-09-20', assigneeId: null }),
      });
      await screen.findByLabelText('Taak');
      choose(oneOffChoice);
      await waitFor(() => expect(within(screen.getByLabelText('Ruimte (optioneel)')).getAllByRole('option')).toHaveLength(2));
      planMode();
      fireEvent.change(screen.getByLabelText('Naam van de klus'), { target: { value: 'Zolder opruimen' } });
      fireEvent.change(screen.getByLabelText('Duur (minuten)'), { target: { value: '90' } });
      expect(screen.getByLabelText('Punten')).toHaveValue(null);
      fireEvent.change(screen.getByLabelText('Punten'), { target: { value: '45' } });
      fireEvent.change(dateField(), { target: { value: '2026-09-20' } });
      fireEvent.click(planSubmit());

      await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
      expect(posts(fetchMock, '/api/v2/occurrences/one-off')).toEqual([
        expect.objectContaining({ name: 'Zolder opruimen', durationMinutes: 90, points: 45, date: '2026-09-20', assigneeId: null }),
      ]);
    });

    it('refuses a day before today next to the date field, linked with aria-describedby', async () => {
      const { fetchMock } = setup();
      await chooseTask('t1');
      planMode();
      fireEvent.change(dateField(), { target: { value: '2026-09-15' } });
      fireEvent.click(planSubmit());
      expect(await screen.findByText('Kies vandaag of een latere dag.')).toBeInTheDocument();
      expect(dateField()).toBeInvalid();
      expect(dateField()).toHaveAccessibleDescription('Kies vandaag of een latere dag.');
      expect(dateField()).toHaveFocus();
      expect(anyPost(fetchMock)).toEqual([]);

      fireEvent.change(dateField(), { target: { value: '' } });
      fireEvent.click(planSubmit());
      expect(await screen.findByText('Kies een datum.')).toBeInTheDocument();
    });

    it('shows the cycle-not-generated refusal of the server under the date field and keeps the dialog open', async () => {
      const { fetchMock, onRecorded, onOpenChange } = setup();
      refusePlanning(fetchMock, 'cycle_not_generated', 409);
      await chooseTask('t1');
      planMode();
      fireEvent.change(dateField(), { target: { value: '2027-03-01' } });
      fireEvent.click(planSubmit());

      const message = 'Die dag valt nog niet in een aangemaakte cyclus. Kies een eerdere dag.';
      expect(await screen.findByText(message)).toBeInTheDocument();
      expect(dateField()).toBeInvalid();
      expect(dateField()).toHaveAccessibleDescription(message);
      expect(dateField()).toHaveFocus();
      expect(onRecorded).not.toHaveBeenCalled();
      expect(onOpenChange).not.toHaveBeenCalled();
      expect(screen.queryByText('Vastleggen is niet gelukt. Probeer het opnieuw.')).not.toBeInTheDocument();

      // Changing the day clears the refusal.
      fireEvent.change(dateField(), { target: { value: '2026-09-18' } });
      expect(screen.queryByText(message)).not.toBeInTheDocument();
    });

    it('shows any other 4xx refusal as a planning problem under the date field', async () => {
      const { fetchMock } = setup();
      refusePlanning(fetchMock, 'validation_error', 400);
      await chooseTask('t2');
      planMode();
      fireEvent.click(planSubmit());
      expect(await screen.findByText('Inplannen op deze dag is niet gelukt. Kies een andere dag of probeer het opnieuw.')).toBeInTheDocument();
      expect(dateField()).toBeInvalid();
    });

    it('a double click creates one planned record', async () => {
      const reply = deferred<Occurrence>();
      const { fetchMock, onRecorded } = setup({ 'POST /api/v2/occurrences': () => reply.promise });
      await chooseTask('t2');
      planMode();
      const button = planSubmit();
      fireEvent.click(button);
      fireEvent.click(button);
      expect(await screen.findByRole('button', { name: 'Bezig met inplannen…' })).toBeDisabled();
      expect(posts(fetchMock, '/api/v2/occurrences')).toHaveLength(1);
      reply.resolve(makeOccurrenceV2({ id: 'new1', taskId: 't2', origin: 'adhoc' }));
      await waitFor(() => expect(onRecorded).toHaveBeenCalledTimes(1));
      expect(posts(fetchMock, '/api/v2/occurrences')).toHaveLength(1);
    });

    it('only mentions a task that is still planned today when recording as done', async () => {
      const PLANNED = makeOccurrenceV2({ id: 'o-planned', taskId: 't1', taskNameSnapshot: 'Stofzuigen', date: TODAY, assigneeId: ANNA._id });
      setup({ '/api/v2/occurrences': page([PLANNED]) });
      await chooseTask('t1');
      expect(await screen.findByRole('group', { name: /staat vandaag nog open/ })).toBeInTheDocument();
      planMode();
      expect(screen.queryByRole('group', { name: /staat vandaag nog open/ })).not.toBeInTheDocument();
      expect(within(dialog()).queryByRole('button', { name: 'Afvinken' })).not.toBeInTheDocument();
      expect(planSubmit()).toBeInTheDocument();
    });
  });

  it('closes on cancel without sending anything', async () => {
    const { fetchMock, onOpenChange } = setup();
    await screen.findByLabelText('Taak');
    fireEvent.click(within(dialog()).getByRole('button', { name: 'Annuleren' }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
    expect(anyPost(fetchMock)).toEqual([]);
  });
});
