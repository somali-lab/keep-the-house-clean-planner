import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { SettingsPage } from './SettingsPage.tsx';

function setup() {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/rooms': [],
    '/api/settings': makeSettings(),
    '/api/ai/prompt-info': {
      actions: {
        planProposal: { fixedPrompt: 'Vaste voorstelprompt', dynamicData: 'Alle taken en personen.' },
        planRebalance: { fixedPrompt: 'Vaste herverdeelprompt', dynamicData: 'Huidige indelingen.' },
        taskSuggestions: { fixedPrompt: 'Vaste takenprompt', dynamicData: 'Ruimte en taken.' },
        planExplanation: { fixedPrompt: 'Vaste uitlegprompt', dynamicData: 'Plan en minuten.' },
      },
    },
    'PATCH /api/settings': (init: RequestInit) => makeSettings(JSON.parse(String(init.body))),
    'POST /api/ai/test': { ok: true },
  });
}

const patchBody = (fetchMock: ReturnType<typeof mockApi>) => {
  const call = fetchMock.mock.calls.find(([u, init]) => u === '/api/settings' && (init as RequestInit | undefined)?.method === 'PATCH');
  return call ? JSON.parse(String((call[1] as RequestInit).body)) : undefined;
};

/** The page has several forms with a save button. */
const aiForm = () => screen.getByRole('form', { name: 'AI-assistent' });

describe('SettingsPage — AI provider', () => {
  it('has no field for the API key and explains it comes from the environment', async () => {
    setup();
    const { container } = renderWithProviders(<SettingsPage />);
    expect(await screen.findByText(/AI_API_KEY/)).toBeInTheDocument();
    expect(container.querySelector('input[type="password"]')).toBeNull();
    expect(screen.queryByLabelText(/sleutel/i)).not.toBeInTheDocument();
  });

  it('asks only for the fields the chosen provider needs, and saves them', async () => {
    const fetchMock = setup();
    renderWithProviders(<SettingsPage />);
    const provider = await screen.findByLabelText('AI-provider');
    expect(screen.queryByLabelText('Endpoint')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Model')).not.toBeInTheDocument();

    fireEvent.change(provider, { target: { value: 'anthropic' } });
    expect(screen.queryByLabelText('Endpoint')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'claude-opus-5' } });

    fireEvent.change(provider, { target: { value: 'openai-compatible' } });
    fireEvent.change(screen.getByLabelText('Endpoint'), { target: { value: 'https://llm.example/v1' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() =>
      expect(patchBody(fetchMock)).toEqual({
        aiProvider: { type: 'openai-compatible', endpoint: 'https://llm.example/v1', model: 'claude-opus-5' },
      }),
    );
    expect(await screen.findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('turns AI off without sending endpoint or model', async () => {
    const fetchMock = setup();
    renderWithProviders(<SettingsPage />);
    fireEvent.change(await screen.findByLabelText('AI-provider'), { target: { value: 'ollama' } });
    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'llama3.2' } });
    fireEvent.change(screen.getByLabelText('AI-provider'), { target: { value: 'none' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'Opslaan' }));
    await waitFor(() => expect(patchBody(fetchMock)).toEqual({ aiProvider: { type: 'none' } }));
  });

  it('shows and saves a configurable Ollama timeout', async () => {
    const fetchMock = setup();
    renderWithProviders(<SettingsPage />);
    fireEvent.change(await screen.findByLabelText('AI-provider'), { target: { value: 'ollama' } });
    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'qwen3:8b' } });
    const timeout = screen.getByLabelText('Time-out (seconden)');
    expect(timeout).toHaveValue(180);
    fireEvent.change(timeout, { target: { value: '240' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() =>
      expect(patchBody(fetchMock)).toEqual({
        aiProvider: { type: 'ollama', model: 'qwen3:8b', timeoutSeconds: 240 },
      }),
    );
  });

  it('does not save an invalid Ollama timeout', async () => {
    const fetchMock = setup();
    renderWithProviders(<SettingsPage />);
    fireEvent.change(await screen.findByLabelText('AI-provider'), { target: { value: 'ollama' } });
    fireEvent.change(screen.getByLabelText('Time-out (seconden)'), { target: { value: '9' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'Opslaan' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('10 tot en met 900');
    expect(patchBody(fetchMock)).toBeUndefined();
  });

  it('tests the current form settings without saving them first', async () => {
    const fetchMock = setup();
    renderWithProviders(<SettingsPage />);
    fireEvent.change(await screen.findByLabelText('AI-provider'), { target: { value: 'ollama' } });
    fireEvent.change(screen.getByLabelText('Endpoint'), { target: { value: 'http://host.docker.internal:11434' } });
    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'qwen3:8b' } });
    fireEvent.change(screen.getByLabelText('Time-out (seconden)'), { target: { value: '240' } });
    fireEvent.click(within(aiForm()).getByRole('button', { name: 'AI-instellingen testen' }));

    await waitFor(() => {
      const call = fetchMock.mock.calls.find(
        ([url, init]) => url === '/api/ai/test' && (init as RequestInit | undefined)?.method === 'POST',
      );
      expect(JSON.parse(String((call?.[1] as RequestInit).body))).toEqual({
        aiProvider: {
          type: 'ollama',
          endpoint: 'http://host.docker.internal:11434',
          model: 'qwen3:8b',
          timeoutSeconds: 240,
        },
      });
    });
    expect(await screen.findByRole('status')).toHaveTextContent('AI-verbinding gelukt');
    expect(patchBody(fetchMock)).toBeUndefined();
  });
});
