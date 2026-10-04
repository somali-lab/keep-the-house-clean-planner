import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { AiPromptTemplates } from '../../api/v2/household.ts';
import { ANNA, BRAM, householdRoutes, mockApi, problem, requestsTo, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { AiPromptsPage } from './AiPromptsPage.tsx';

const template = (name: string) => ({ system: `${name} system {{schema}}`, user: `${name} user {{input}}` });
const defaults: AiPromptTemplates = {
  planProposal: template('voorstel'),
  planRebalance: template('herverdelen'),
  taskSuggestions: template('taken'),
  planExplanation: template('uitleg'),
};

function setup(patch: unknown = { ...makeSettings({ version: 6 }), version: 7 }) {
  storeProfile(ANNA.id);
  return mockApi({
    ...householdRoutes([ANNA, BRAM], makeSettings({ version: 6 })),
    '/api/v2/ai/prompt-info': {
      defaults,
      actions: Object.fromEntries(
        Object.entries(defaults).map(([key, value]) => [key, { ...value, fixedPrompt: value.system, dynamicData: `Data voor ${key}` }]),
      ),
    },
    'PATCH /api/v2/settings': patch,
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

    await waitFor(() => expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')).toHaveLength(1));
    const [request] = requestsTo(fetchMock, 'PATCH', '/api/v2/settings');
    expect((request!.body as { aiPromptTemplates: AiPromptTemplates }).aiPromptTemplates.planProposal).toEqual({
      system: 'Mijn systemprompt {{schema}}',
      user: 'Plan dit: {{input}}',
    });
    expect(Object.keys((request!.body as { aiPromptTemplates: object }).aiPromptTemplates)).toEqual([
      'planProposal',
      'planRebalance',
      'taskSuggestions',
      'planExplanation',
    ]);
    expect(request!.headers['if-match']).toBe('"6"');
    expect(await screen.findByRole('status')).toBeInTheDocument();
  });

  it('keeps the edited prompts and says so when the settings changed in the meantime (412)', async () => {
    setup(problem(412, 'precondition_failed', 'The settings changed.'));
    renderWithProviders(<AiPromptsPage />);
    fireEvent.change(await screen.findByLabelText('Systemprompt'), { target: { value: 'Mijn systemprompt {{schema}}' } });
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    expect(screen.getByLabelText('Systemprompt')).toHaveValue('Mijn systemprompt {{schema}}');
  });

  it('refuses to save when a required placeholder is removed', async () => {
    const fetchMock = setup();
    renderWithProviders(<AiPromptsPage />);
    fireEvent.change(await screen.findByLabelText('Userprompt'), { target: { value: 'Geen invoer meer' } });
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('{{input}}');
    expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')).toEqual([]);
  });
});
