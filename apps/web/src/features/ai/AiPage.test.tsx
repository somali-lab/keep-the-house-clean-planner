import type { CyclePlan } from '@huishoudplanner/shared';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeRoom, makeSettings, renderWithProviders } from '../../test/render.tsx';
import type { PlanDiffResponse } from './api.ts';
import { AiPage } from './AiPage.tsx';

const STAMP = '2026-09-14T08:00:00.000Z';
const plan = (overrides: Partial<CyclePlan> & Pick<CyclePlan, '_id' | 'name'>): CyclePlan => ({
  active: false,
  slots: [],
  weekThemes: ['', '', '', ''],
  draft: false,
  source: 'manual',
  proposalId: null,
  rationale: null,
  discarded: false,
  createdAt: STAMP,
  updatedAt: STAMP,
  ...overrides,
});

const ACTIVE = plan({ _id: 'p-active', name: 'Standaard', active: true });
const DRAFT = plan({
  _id: 'p-draft',
  name: 'AI-voorstel 2026-09-16',
  draft: true,
  source: 'ai',
  proposalId: 'prop-1',
  rationale: ['Week 1: rustig.', 'Week 2: meer badkamer.', 'Week 3: ramen.', 'Week 4: gelijk verdeeld.'],
});

const week = (weekIndex: number, anna: number, bram: number) => ({
  weekIndex,
  users: [
    { userId: ANNA._id, minutes: anna },
    { userId: BRAM._id, minutes: bram },
  ],
  unassignedMinutes: 0,
});

const DIFF: PlanDiffResponse = {
  planId: 'p-draft',
  againstPlanId: 'p-active',
  added: [{ taskId: 't3', taskName: 'Ramen lappen', roomName: 'Woonkamer', durationMinutes: 60, weekIndex: 2, weekday: 5, assigneeId: null }],
  removed: [{ taskId: 't2', taskName: 'Wastafel', roomName: 'Badkamer', durationMinutes: 10, weekIndex: 2, weekday: 6, assigneeId: BRAM._id }],
  moved: [
    {
      taskId: 't1',
      taskName: 'Badkamer',
      roomName: 'Badkamer',
      durationMinutes: 30,
      from: { weekIndex: 1, weekday: 1, assigneeId: ANNA._id },
      to: { weekIndex: 1, weekday: 2, assigneeId: BRAM._id },
    },
    {
      taskId: 't2',
      taskName: 'Wastafel',
      roomName: 'Badkamer',
      durationMinutes: 10,
      from: { weekIndex: 0, weekday: 3, assigneeId: BRAM._id },
      to: { weekIndex: 0, weekday: 3, assigneeId: ANNA._id },
    },
  ],
  unchanged: 1,
  summary: {
    before: [week(0, 30, 10), week(1, 30, 0), week(2, 0, 10), week(3, 0, 0)],
    after: [week(0, 40, 0), week(1, 0, 30), week(2, 0, 0), week(3, 0, 0)],
  },
  warnings: [{ code: 'interval_mismatch', taskId: 't2', placed: 1, required: 8 }],
};

function setup(aiType: 'none' | 'mock') {
  storeProfile(ANNA._id);
  let plans = [ACTIVE, DRAFT];
  return mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/settings': makeSettings({ aiProvider: { type: aiType } }),
    '/api/rooms': [makeRoom({ _id: 'r1', name: 'Keuken' })],
    '/api/cycle-plans': () => plans,
    '/api/cycle-plans/p-draft/diff': DIFF,
    'POST /api/ai/propose-plan': { planId: 'p-draft', proposalId: 'prop-1', warnings: [], rationale: DRAFT.rationale },
    'POST /api/ai/explain': { rationale: ['Week 1 rustig.', 'Week 2 verdeeld.', 'Week 3 logisch.', 'Week 4 eerlijk.'] },
    'POST /api/cycle-plans/p-draft/apply-proposal': () => {
      plans = [{ ...ACTIVE, active: false }, { ...DRAFT, active: true, draft: false }];
      return { plan: plans[1] };
    },
    'POST /api/cycle-plans/p-draft/discard': () => {
      plans = [ACTIVE, { ...DRAFT, discarded: true }];
      return plans[1];
    },
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

    await waitFor(() => expect(postBody(fetchMock, '/api/ai/propose-plan')).toEqual({ constraints: 'geen nat werk doordeweeks' }));
    expect(await screen.findByText('Plan aangemaakt. Bekijk, activeer of verwijder het via Plannen beheren.')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Voorstel bekijken' })).not.toBeInTheDocument();
  });

  it('shows an elapsed-seconds counter while Ollama is thinking', async () => {
    const mocked = setup('mock');
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        if (String(input) === '/api/ai/propose-plan') return new Promise<Response>(() => undefined);
        return mocked(input, init);
      }),
    );
    renderWithProviders(<AiPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Voorstel maken' }));
    expect(await screen.findByRole('status')).toHaveTextContent('De AI denkt na… 0 sec');
  });

  it('keeps showing the running request and timer after leaving and returning to the page', async () => {
    const mocked = setup('mock');
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        if (String(input) === '/api/ai/propose-plan') return new Promise<Response>(() => undefined);
        return mocked(input, init);
      }),
    );
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
    setup('mock');
    renderWithProviders(<AiPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Uitleg bij actief plan' }));

    expect(await screen.findByText('Uitleg is klaar en staat hieronder.')).toBeInTheDocument();
    const explanation = await screen.findByRole('region', { name: 'Uitleg per week' });
    expect(within(explanation).getByText('Week 1 rustig.')).toBeInTheDocument();
  });

  it('explains a plan that failed validation twice in plain language', async () => {
    const mocked = setup('mock');
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) =>
        String(input) === '/api/ai/propose-plan'
          ? new Response(JSON.stringify({ code: 'ai_invalid_plan', errors: ['assignee_unavailable (slot 0)'] }), { status: 422 })
          : mocked(input, init),
      ),
    );
    renderWithProviders(<AiPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Voorstel maken' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Het voorstel voldeed niet aan de regels, ook niet na een tweede poging.');
  });
});
