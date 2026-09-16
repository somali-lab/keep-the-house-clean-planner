import type { AiPromptTemplates } from '@huishoudplanner/shared';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { AiPromptsPage } from './AiPromptsPage.tsx';

const template = (name: string) => ({ system: `${name} system {{schema}}`, user: `${name} user {{input}}` });
const defaults: AiPromptTemplates = {
  planProposal: template('voorstel'),
  planRebalance: template('herverdelen'),
  taskSuggestions: template('taken'),
  planExplanation: template('uitleg'),
};

function setup() {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': makeSettings(),
    '/api/ai/prompt-info': {
      defaults,
      actions: Object.fromEntries(
        Object.entries(defaults).map(([key, value]) => [key, { ...value, dynamicData: `Data voor ${key}` }]),
      ),
    },
    'PATCH /api/settings': (init: RequestInit) => makeSettings(JSON.parse(String(init.body))),
  });
}

describe('AiPromptsPage', () => {
  it('opens a directly linked prompt tab', async () => {
    setup();
    renderWithProviders(<AiPromptsPage />, { route: '/ai-prompts?tab=tasks' });

    expect(await screen.findByRole('tab', { name: 'Taken voorstellen' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    expect(screen.getByLabelText('Systemprompt')).toHaveValue('taken system {{schema}}');
  });

  it('shows system and user prompts per AI action and saves all templates', async () => {
    const fetchMock = setup();
    renderWithProviders(<AiPromptsPage />);

    expect(await screen.findByLabelText('Systemprompt')).toHaveValue('voorstel system {{schema}}');
    fireEvent.change(screen.getByLabelText('Systemprompt'), { target: { value: 'Mijn systemprompt {{schema}}' } });
    fireEvent.change(screen.getByLabelText('Userprompt'), { target: { value: 'Plan dit: {{input}}' } });
    fireEvent.click(screen.getByRole('tab', { name: 'Taken voorstellen' }));
    expect(screen.getByLabelText('Systemprompt')).toHaveValue('taken system {{schema}}');
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));

    await waitFor(() => {
      const call = fetchMock.mock.calls.find(([url, init]) => url === '/api/settings' && (init as RequestInit | undefined)?.method === 'PATCH');
      expect(JSON.parse(String((call?.[1] as RequestInit).body)).aiPromptTemplates.planProposal).toEqual({
        system: 'Mijn systemprompt {{schema}}',
        user: 'Plan dit: {{input}}',
      });
    });
  });

  it('refuses to save when a required placeholder is removed', async () => {
    const fetchMock = setup();
    renderWithProviders(<AiPromptsPage />);
    fireEvent.change(await screen.findByLabelText('Userprompt'), { target: { value: 'Geen invoer meer' } });
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('{{input}}');
    expect(fetchMock.mock.calls.some(([url, init]) => url === '/api/settings' && (init as RequestInit | undefined)?.method === 'PATCH')).toBe(false);
  });
});
