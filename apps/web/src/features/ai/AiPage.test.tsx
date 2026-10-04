import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, page, problem, storeProfile } from '../../test/fixtures.ts';
import { makePlanV2 } from '../../test/plans.ts';
import { makeRoomV2, makeSettings, renderWithProviders } from '../../test/render.tsx';
import { AiPage } from './AiPage.tsx';

const ACTIVE = makePlanV2({ id: 'p-active', name: 'Standaard', active: true });
const DRAFT = makePlanV2({
  id: 'p-draft',
  name: 'AI-voorstel 2026-09-16',
  draft: true,
  source: 'ai',
  proposalId: 'prop-1',
  rationale: ['Week 1: rustig.', 'Week 2: meer badkamer.', 'Week 3: ramen.', 'Week 4: gelijk verdeeld.'],
});

function setup(aiType: 'none' | 'mock', extra: Record<string, unknown> = {}) {
  storeProfile(ANNA._id);
  const plans = [ACTIVE, DRAFT];
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': makeSettings({ aiProvider: { type: aiType } }),
    '/api/v2/rooms': page([makeRoomV2({ id: 'r1', name: 'Keuken' })]),
    '/api/v2/cycle-plans': () => page(plans),
    'POST /api/v2/ai/propose-plan': { planId: 'p-draft', proposalId: 'prop-1', warnings: [], rationale: DRAFT.rationale },
    'POST /api/v2/ai/rebalance': { planId: 'p-draft', proposalId: 'prop-2', warnings: [], rationale: DRAFT.rationale },
    'POST /api/v2/ai/suggest-tasks': { suggestions: [{ name: 'Fornuis poetsen', intervalKey: '1w', durationMinutes: '20', notes: 'Ook de knoppen' }] },
    'POST /api/v2/tasks': { id: 't9', name: 'Fornuis poetsen' },
    'POST /api/v2/ai/explain': { rationale: ['Week 1 rustig.', 'Week 2 verdeeld.', 'Week 3 logisch.', 'Week 4 eerlijk.'] },
    ...extra,
  });
}

const postBody = (fetchMock: ReturnType<typeof mockApi>, url: string) => {
  const call = fetchMock.mock.calls.find(([u, init]) => u === url && (init as RequestInit | undefined)?.method === 'POST');
  return call ? JSON.parse(String((call[1] as RequestInit).body)) : undefined;
};

describe('AiPage', () => {
  it('hides the AI actions and explains how to switch it on when AI is off', async () => {
    setup('none');
    renderWithProviders(<AiPage />);
    expect(await screen.findByText(/De AI-assistent staat uit/)).toBeInTheDocument();
    expect(screen.getByText(/AI_API_KEY/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Naar Instellingen' })).toHaveAttribute('href', '/manage/settings');
    expect(screen.queryByRole('button', { name: 'Voorstel maken' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Herbalanceer actief plan' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Taken voorstellen' })).not.toBeInTheDocument();
  });

  it('creates a draft plan with the constraints and leaves review to plan management', async () => {
    const fetchMock = setup('mock');
    renderWithProviders(<AiPage />);
    fireEvent.change(await screen.findByLabelText('Wensen en beperkingen'), { target: { value: 'geen nat werk doordeweeks' } });
    fireEvent.click(screen.getByRole('button', { name: 'Voorstel maken' }));

    // Only the constraints: `taskIds` stays out (all active tasks), an explicit null would be refused.
    await waitFor(() => expect(postBody(fetchMock, '/api/v2/ai/propose-plan')).toEqual({ constraints: 'geen nat werk doordeweeks' }));
    expect(await screen.findByText('Plan aangemaakt. Bekijk, activeer of verwijder het via Plannen beheren.')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Voorstel bekijken' })).not.toBeInTheDocument();
  });

  it('sends an empty body for a proposal without constraints', async () => {
    const fetchMock = setup('mock');
    renderWithProviders(<AiPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Voorstel maken' }));

    await waitFor(() => expect(postBody(fetchMock, '/api/v2/ai/propose-plan')).toEqual({}));
  });

  it('rebalances the active plan, leaving the constraints out when there are none', async () => {
    const fetchMock = setup('mock');
    renderWithProviders(<AiPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Herbalanceer actief plan' }));
    await waitFor(() => expect(postBody(fetchMock, '/api/v2/ai/rebalance')).toEqual({ planId: 'p-active' }));
  });

  it('shows an elapsed-seconds counter while Ollama is thinking', async () => {
    setup('mock', { 'POST /api/v2/ai/propose-plan': () => new Promise<Response>(() => undefined) });
    renderWithProviders(<AiPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Voorstel maken' }));
    expect(await screen.findByRole('status')).toHaveTextContent('De AI denkt na… 0 sec');
  });

  it('keeps showing the running request and timer after leaving and returning to the page', async () => {
    setup('mock', { 'POST /api/v2/ai/propose-plan': () => new Promise<Response>(() => undefined) });
    const first = renderWithProviders(<AiPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Voorstel maken' }));
    expect(await screen.findByRole('status')).toHaveTextContent(/De AI denkt na… \d+ sec/);

    const queryClient = first.queryClient;
    first.unmount();
    renderWithProviders(<AiPage />, { queryClient });

    expect(await screen.findByRole('button', { name: 'Voorstel maken' })).toBeDisabled();
    expect(screen.getByRole('status')).toHaveTextContent(/De AI denkt na… \d+ sec/);
  });

  it('confirms when the active-plan explanation is ready and shows it directly below', async () => {
    const fetchMock = setup('mock');
    renderWithProviders(<AiPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Uitleg bij actief plan' }));

    expect(await screen.findByText('Uitleg is klaar en staat hieronder.')).toBeInTheDocument();
    const explanation = await screen.findByRole('region', { name: 'Uitleg per week' });
    expect(within(explanation).getByText('Week 1 rustig.')).toBeInTheDocument();
    expect(postBody(fetchMock, '/api/v2/ai/explain')).toEqual({ planId: 'p-active' });
  });

  it('explains a plan that failed validation twice in plain language', async () => {
    setup('mock', {
      'POST /api/v2/ai/propose-plan': () => problem(422, 'ai_invalid_plan', 'invalid', { errors: ['assignee_unavailable (slot 0)'] }),
    });
    renderWithProviders(<AiPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Voorstel maken' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Het voorstel voldeed niet aan de regels, ook niet na een tweede poging.');
  });

  it('suggests tasks for a room and adds one through the tasks endpoint, without points or empty members', async () => {
    const fetchMock = setup('mock');
    renderWithProviders(<AiPage section="tasks" />);
    fireEvent.change(await screen.findByLabelText('Ruimte'), { target: { value: 'r1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Taken voorstellen' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Fornuis poetsen toevoegen als taak' }));

    await waitFor(() => expect(postBody(fetchMock, '/api/v2/tasks')).toBeDefined());
    expect(postBody(fetchMock, '/api/v2/ai/suggest-tasks')).toEqual({ roomId: 'r1' });
    expect(postBody(fetchMock, '/api/v2/tasks')).toEqual({
      name: 'Fornuis poetsen',
      roomId: 'r1',
      intervalKey: '1w',
      durationMinutes: 20,
      notes: 'Ook de knoppen',
    });
    expect(await screen.findByText('Toegevoegd')).toBeInTheDocument();
  });
});
