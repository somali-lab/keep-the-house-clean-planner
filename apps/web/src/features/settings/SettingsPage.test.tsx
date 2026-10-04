import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, LIMITS, householdRoutes, mockApi, page, problem, requestsTo, sequence, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { SettingsPage } from './SettingsPage.tsx';

function setup(overrides: Record<string, unknown> = {}, settings = makeSettings({ version: 3 })) {
  storeProfile(ANNA.id);
  return mockApi({
    ...householdRoutes([ANNA, BRAM], settings),
    '/api/v2/meta/limits': LIMITS,
    '/api/v2/rooms': page([]),
    '/api/v2/tasks': page([]),
    'PATCH /api/v2/settings': { ...settings, version: 4 },
    'POST /api/v2/ai/test': { ok: true },
    'POST /api/v2/jobs/generation': {
      runId: 'run1',
      removed: 7,
      generated: [
        { cycleIndex: 0, cycleId: 'c1', planId: 'p1', inserted: 2, skipped: 0 },
        { cycleIndex: 1, cycleId: 'c2', planId: 'p1', inserted: '3', skipped: 0 },
      ],
      due: { due: 4, overdue: 1 },
    },
    'POST /api/v2/points/recompute': {
      trigger: 'admin',
      tasksDefaulted: 0,
      snapshotsSet: 1,
      created: 1,
      updated: 1,
      removed: 1,
      bonusesCreated: 1,
      bonusesRemoved: 0,
    },
    'POST /api/v2/jobs/audit-retention': { status: 'done', cutoff: '2026-08-17T08:00:00.000Z', deleted: 6 },
    'POST /api/v2/jobs/morning-notify': { status: 'done', date: '2026-09-16', sent: 2, failed: 0, quiet: 1 },
    ...overrides,
  });
}

const patches = (fetchMock: ReturnType<typeof mockApi>) => requestsTo(fetchMock, 'PATCH', '/api/v2/settings');

/** The page has several forms with a save button. */
const aiForm = () => screen.getByRole('form', { name: 'AI-assistent' });
const renderSettings = async () => {
  const result = renderWithProviders(<SettingsPage initialTab="ai" />);
  await screen.findByLabelText('AI-provider');
  return result;
};

describe('SettingsPage — AI provider', () => {
  it('keeps every main settings tab on one horizontally scrollable row', async () => {
    setup();
    await renderSettings();
    const tabList = screen.getAllByRole('tablist')[0]!;
    expect(tabList).toHaveClass('flex-nowrap', 'overflow-x-auto', 'overflow-y-hidden');
    expect(within(tabList).getAllByRole('tab')).toHaveLength(7);
    expect(within(tabList).getAllByRole('tab').every((tab) => tab.classList.contains('flex-none'))).toBe(true);
  });

  it('has no field for the API key and explains it comes from the environment', async () => {
    setup();
    const { container } = await renderSettings();
    expect(await screen.findByText(/AI_API_KEY/)).toBeInTheDocument();
    expect(container.querySelector('input[type="password"]')).toBeNull();
    expect(screen.queryByLabelText(/sleutel/i)).not.toBeInTheDocument();
  });

  it('asks only for the fields the chosen provider needs, and saves them with the version of the settings', async () => {
    const fetchMock = setup();
    await renderSettings();
    const provider = await screen.findByLabelText('AI-provider');
    expect(screen.queryByLabelText('Endpoint')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Model')).not.toBeInTheDocument();

    fireEvent.change(provider, { target: { value: 'anthropic' } });
    expect(screen.queryByLabelText('Endpoint')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'claude-opus-5' } });

    fireEvent.change(provider, { target: { value: 'openai-compatible' } });
    fireEvent.change(screen.getByLabelText('Endpoint'), { target: { value: 'https://llm.example/v1' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() => expect(patches(fetchMock)).toHaveLength(1));
    expect(patches(fetchMock)[0]!.body).toEqual({
      aiProvider: { type: 'openai-compatible', endpoint: 'https://llm.example/v1', model: 'claude-opus-5' },
    });
    expect(patches(fetchMock)[0]!.headers['if-match']).toBe('"3"');
    expect(await screen.findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('turns AI off without sending endpoint or model', async () => {
    const fetchMock = setup();
    await renderSettings();
    fireEvent.change(await screen.findByLabelText('AI-provider'), { target: { value: 'ollama' } });
    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'llama3.2' } });
    fireEvent.change(screen.getByLabelText('AI-provider'), { target: { value: 'none' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(patches(fetchMock)).toHaveLength(1));
    expect(patches(fetchMock)[0]!.body).toEqual({ aiProvider: { type: 'none' } });
  });

  it('shows and saves a configurable Ollama timeout', async () => {
    const fetchMock = setup();
    await renderSettings();
    fireEvent.change(await screen.findByLabelText('AI-provider'), { target: { value: 'ollama' } });
    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'qwen3:8b' } });
    const timeout = screen.getByLabelText('Time-out (seconden)');
    expect(timeout).toHaveValue(180);
    fireEvent.change(timeout, { target: { value: '240' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() => expect(patches(fetchMock)).toHaveLength(1));
    expect(patches(fetchMock)[0]!.body).toEqual({ aiProvider: { type: 'ollama', model: 'qwen3:8b', timeoutSeconds: 240 } });
  });

  it('does not save an invalid Ollama timeout', async () => {
    const fetchMock = setup();
    await renderSettings();
    fireEvent.change(await screen.findByLabelText('AI-provider'), { target: { value: 'ollama' } });
    fireEvent.change(screen.getByLabelText('Time-out (seconden)'), { target: { value: '9' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'Opslaan' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('10 tot en met 900');
    expect(patches(fetchMock)).toEqual([]);
  });

  it('tests the current form settings without saving them first', async () => {
    const fetchMock = setup();
    await renderSettings();
    fireEvent.change(await screen.findByLabelText('AI-provider'), { target: { value: 'ollama' } });
    fireEvent.change(screen.getByLabelText('Endpoint'), { target: { value: 'http://host.docker.internal:11434' } });
    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'qwen3:8b' } });
    fireEvent.change(screen.getByLabelText('Time-out (seconden)'), { target: { value: '240' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'AI-instellingen testen' }));

    await waitFor(() => expect(requestsTo(fetchMock, 'POST', '/api/v2/ai/test')).toHaveLength(1));
    expect(requestsTo(fetchMock, 'POST', '/api/v2/ai/test')[0]!.body).toEqual({
      aiProvider: {
        type: 'ollama',
        endpoint: 'http://host.docker.internal:11434',
        model: 'qwen3:8b',
        timeoutSeconds: 240,
      },
    });
    expect(await screen.findByRole('status')).toHaveTextContent('AI-verbinding gelukt');
    expect(patches(fetchMock)).toEqual([]);
  });

  it('shows why the test failed', async () => {
    setup({ 'POST /api/v2/ai/test': problem(502, 'ai_provider_error', 'The provider refused the key.') });
    await renderSettings();
    fireEvent.change(await screen.findByLabelText('AI-provider'), { target: { value: 'mock' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'AI-instellingen testen' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('The provider refused the key.');
  });

  it('keeps the form and reads the settings again when they changed in the meantime (412)', async () => {
    const fetchMock = setup({ 'PATCH /api/v2/settings': problem(412, 'precondition_failed', 'The settings changed.') });
    await renderSettings();
    fireEvent.change(await screen.findByLabelText('AI-provider'), { target: { value: 'mock' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'Opslaan' }));

    expect(await within(aiForm()).findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    expect(screen.getByLabelText('AI-provider')).toHaveValue('mock');
    await waitFor(() => expect(requestsTo(fetchMock, 'GET', '/api/v2/settings').length).toBeGreaterThan(1));
  });
});

describe('SettingsPage — interface', () => {
  it('saves whether an open circle or thumb completes a task', async () => {
    const fetchMock = setup();
    renderWithProviders(<SettingsPage initialTab="interface" />);
    fireEvent.click(await screen.findByLabelText('Duimpje omhoog'));
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(patches(fetchMock)).toHaveLength(1));
    expect(patches(fetchMock)[0]!.body).toEqual({ completionControl: 'thumb' });
    expect(patches(fetchMock)[0]!.headers['if-match']).toBe('"3"');
    expect(await screen.findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('keeps the choice and says so when the settings changed in the meantime (412)', async () => {
    setup({ 'PATCH /api/v2/settings': problem(412, 'precondition_failed', 'The settings changed.') });
    renderWithProviders(<SettingsPage initialTab="interface" />);
    fireEvent.click(await screen.findByLabelText('Duimpje omhoog'));
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    expect(screen.getByLabelText('Duimpje omhoog')).toBeChecked();
  });
});

describe('SettingsPage — jobs', () => {
  it('runs every scheduled job manually and shows its result', async () => {
    const fetchMock = setup();
    renderWithProviders(<SettingsPage initialTab="jobs" />);
    expect(await screen.findByRole('heading', { name: 'Geplande jobs' })).toBeInTheDocument();

    const buttons = screen.getAllByRole('button', { name: 'Nu starten' });
    fireEvent.click(buttons[0]!);
    expect(
      await screen.findByText(
        'Klaar: 7 oude taken verwijderd, 5 taken gegenereerd, 4 aan de beurt en 1 flink achter.',
      ),
    ).toBeInTheDocument();

    fireEvent.click(buttons[1]!);
    expect(await screen.findByText('Klaar: 2 aangemaakt, 2 bijgewerkt en 1 verwijderd.')).toBeInTheDocument();

    fireEvent.click(buttons[2]!);
    expect(await screen.findByText('Klaar: 6 oude auditregels verwijderd.')).toBeInTheDocument();

    fireEvent.click(buttons[3]!);
    expect(await screen.findByText('Klaar: 2 verstuurd, 0 mislukt en 1 zonder melding.')).toBeInTheDocument();

    const posts = fetchMock.mock.calls
      .filter(([, init]) => (init as RequestInit | undefined)?.method === 'POST')
      .map(([url]) => url);
    expect(posts).toEqual([
      '/api/v2/jobs/generation',
      '/api/v2/points/recompute',
      '/api/v2/jobs/audit-retention',
      '/api/v2/jobs/morning-notify',
    ]);
  });

  it('says nothing changed when the recompute finds no drift', async () => {
    setup({
      'POST /api/v2/points/recompute': {
        trigger: 'admin',
        tasksDefaulted: 0,
        snapshotsSet: 0,
        created: 0,
        updated: 0,
        removed: 0,
        bonusesCreated: 0,
        bonusesRemoved: 0,
      },
    });
    renderWithProviders(<SettingsPage initialTab="jobs" />);
    await screen.findByRole('heading', { name: 'Geplande jobs' });
    fireEvent.click(screen.getAllByRole('button', { name: 'Nu starten' })[1]!);
    expect(await screen.findByText('Klaar: er is niets veranderd.')).toBeInTheDocument();
  });

  it('says so when the retention is switched off and when a job fails', async () => {
    setup({
      'POST /api/v2/jobs/audit-retention': { status: 'disabled' },
      'POST /api/v2/jobs/generation': problem(409, 'conflict', 'A run is in progress.'),
    });
    renderWithProviders(<SettingsPage initialTab="jobs" />);
    await screen.findByRole('heading', { name: 'Geplande jobs' });
    const buttons = screen.getAllByRole('button', { name: 'Nu starten' });
    fireEvent.click(buttons[2]!);
    expect(await screen.findByText('Niet uitgevoerd: er is geen bewaartermijn ingesteld.')).toBeInTheDocument();
    fireEvent.click(buttons[0]!);
    expect(await screen.findByText('De job kon niet worden uitgevoerd.')).toBeInTheDocument();
  });
});

describe('SettingsPage — bonuses', () => {
  it('has the bonus card next to the cycle start and the vacations for an administrator', async () => {
    setup();
    renderWithProviders(<SettingsPage initialTab="calendar" />);
    expect(await screen.findByRole('form', { name: 'Cyclusstart' })).toBeInTheDocument();
    expect(await screen.findByRole('form', { name: 'Bonussen' })).toBeInTheDocument();
    expect(screen.getByLabelText('Week: alles gedaan')).toHaveValue(0);
  });

  it('has the points value card under the bonuses for an administrator', async () => {
    setup();
    renderWithProviders(<SettingsPage initialTab="calendar" />);
    expect(await screen.findByRole('form', { name: 'Puntenwaarde' })).toBeInTheDocument();
    expect(screen.getByLabelText('Valuta')).toHaveValue('EUR');
    expect(screen.getByLabelText('Waarde van één punt (in centen)')).toHaveValue(0);
  });

  it('has the reward goals card under the points value for an administrator', async () => {
    setup();
    renderWithProviders(<SettingsPage initialTab="calendar" />);
    expect(await screen.findByRole('form', { name: 'Beloningsdoelen' })).toBeInTheDocument();
    expect(screen.getByLabelText('Doel per week (punten)')).toHaveValue(null);
    expect(screen.getByLabelText('Doel per cyclus (punten)')).toHaveValue(null);
  });

  it('uses the new version of the settings for the next save after a save', async () => {
    const fetchMock = setup();
    renderWithProviders(<SettingsPage initialTab="calendar" />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.change(within(form).getByLabelText('Waarde van één punt (in centen)'), { target: { value: '12' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    await waitFor(() => expect(patches(fetchMock)).toHaveLength(1));
    await within(form).findByRole('status');
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    await waitFor(() => expect(patches(fetchMock)).toHaveLength(2));
    expect(patches(fetchMock).map((request) => request.headers['if-match'])).toEqual(['"3"', '"4"']);
  });
});

describe('SettingsPage — a retry after a refused save', () => {
  const STALE = () => problem(412, 'precondition_failed', 'The settings changed.');
  const rereadAndRetry = async (form: HTMLElement, button: string, fetchMock: ReturnType<typeof mockApi>) => {
    await waitFor(() => expect(requestsTo(fetchMock, 'GET', '/api/v2/settings').length).toBeGreaterThan(1));
    // The version read again reaches the form before the next click.
    await waitFor(() => expect(within(form).getByRole('alert')).toBeInTheDocument());
    fireEvent.click(within(form).getByRole('button', { name: button }));
    await waitFor(() => expect(patches(fetchMock)).toHaveLength(2));
  };

  it('sends the version that was read again when the person saves a second time after a 412', async () => {
    const fetchMock = setup({
      '/api/v2/settings': sequence(makeSettings({ version: 3 }), makeSettings({ version: 5 })),
      'PATCH /api/v2/settings': sequence(STALE, makeSettings({ version: 6 })),
    });
    renderWithProviders(<SettingsPage initialTab="calendar" />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.change(within(form).getByLabelText('Waarde van één punt (in centen)'), { target: { value: '12' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    await rereadAndRetry(form, 'Puntenwaarde opslaan', fetchMock);
    expect(patches(fetchMock).map((request) => request.headers['if-match'])).toEqual(['"3"', '"5"']);
    expect(within(form).getByLabelText('Waarde van één punt (in centen)')).toHaveValue(12);
  });

  it('reads the settings again after a bonus conflict (409), keeps the edit and retries with the new version', async () => {
    const fetchMock = setup({
      '/api/v2/settings': sequence(makeSettings({ version: 3 }), makeSettings({ version: 5 })),
      'PATCH /api/v2/settings': sequence(() => problem(409, 'bonus_schedule_conflict', 'Conflict.'), makeSettings({ version: 6 })),
    });
    renderWithProviders(<SettingsPage initialTab="calendar" />);
    const form = await screen.findByRole('form', { name: 'Bonussen' });
    fireEvent.change(within(form).getByLabelText('Week: alles gedaan'), { target: { value: '9' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Bonussen opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Iemand anders heeft de bonusbedragen net gewijzigd');
    await rereadAndRetry(form, 'Bonussen opslaan', fetchMock);
    expect(patches(fetchMock).map((request) => request.headers['if-match'])).toEqual(['"3"', '"5"']);
    expect(within(form).getByLabelText('Week: alles gedaan')).toHaveValue(9);
  });

  it('shows the first field error of a refused value instead of the generic message', async () => {
    setup({
      'PATCH /api/v2/settings': () => problem(400, 'validation_error', 'Invalid.', { errors: { currencyCode: ['invalid_currency'] } }),
    });
    renderWithProviders(<SettingsPage initialTab="calendar" />);
    const form = await screen.findByRole('form', { name: 'Puntenwaarde' });
    fireEvent.click(within(form).getByRole('button', { name: 'Puntenwaarde opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Ongeldige waarde voor currencyCode: invalid_currency.');
  });

  it.each([
    ['Cyclusstart', 'Startdatum (maandag)', '2026-10-12', 'Opslaan'],
    ['Puntenwaarde', 'Waarde van één punt (in centen)', '33', 'Puntenwaarde opslaan'],
    ['Beloningsdoelen', 'Doel per week (punten)', '8', 'Doelen opslaan'],
    ['Bonussen', 'Week: alles gedaan', '8', 'Bonussen opslaan'],
  ])('clears the saved message of the %s form as soon as it is edited', async (name, label, value, save) => {
    setup();
    renderWithProviders(<SettingsPage initialTab="calendar" />);
    const form = await screen.findByRole('form', { name });
    fireEvent.click(within(form).getByRole('button', { name: save }));
    expect(await within(form).findByRole('status')).toHaveTextContent('Opgeslagen.');
    fireEvent.change(within(form).getByLabelText(label), { target: { value } });
    expect(within(form).queryByRole('status')).not.toBeInTheDocument();
  });

  it('clears the saved message of the vacations form when a range is removed', async () => {
    setup({}, makeSettings({ vacationRanges: [{ from: '2026-12-21', to: '2027-01-03' }] }));
    renderWithProviders(<SettingsPage initialTab="calendar" />);
    const form = await screen.findByRole('form', { name: 'Vakanties' });
    fireEvent.click(within(form).getByRole('button', { name: 'Vakanties opslaan' }));
    expect(await within(form).findByRole('status')).toBeInTheDocument();
    fireEvent.click(within(form).getByRole('button', { name: /Verwijder vakantie/ }));
    expect(within(form).queryByRole('status')).not.toBeInTheDocument();
  });
});
