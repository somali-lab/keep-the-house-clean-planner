import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, problem, storeProfile } from '../../test/fixtures.ts';
import { renderWithProviders } from '../../test/render.tsx';
import type { PromoteSuggestion } from './api.ts';
import { PromoteBanner } from './PromoteBanner.tsx';

const SUGGESTION: PromoteSuggestion = {
  planId: 'p1',
  taskId: 't1',
  taskName: 'Badkamer',
  fromSlot: { weekIndex: 1, weekday: 2, assigneeId: ANNA._id },
  toWeekday: 3,
  toAssigneeId: null,
  evidence: ['o2', 'o1'],
};

function setup(suggestions: PromoteSuggestion[], extra: Record<string, unknown> = {}) {
  storeProfile(ANNA._id);
  return mockApi({ '/api/users': [ANNA, BRAM], '/api/v2/promote-suggestions': { items: suggestions }, ...extra });
}

const bodyOf = (fetchMock: ReturnType<typeof mockApi>, url: string) => {
  const call = fetchMock.mock.calls.find(([u]) => u === url);
  return call ? JSON.parse(String((call[1] as RequestInit).body)) : undefined;
};

describe('PromoteBanner', () => {
  it('asks whether to change the plan', async () => {
    setup([SUGGESTION]);
    renderWithProviders(<PromoteBanner />);
    expect(await screen.findByText('Je verplaatst ‘Badkamer’ steeds van dinsdag naar woensdag. Plan aanpassen?')).toBeInTheDocument();
  });

  it('mentions the other person when the moves also changed who does it', async () => {
    setup([{ ...SUGGESTION, toWeekday: 4, toAssigneeId: BRAM._id }]);
    renderWithProviders(<PromoteBanner />);
    expect(
      await screen.findByText('Je verplaatst ‘Badkamer’ steeds van dinsdag naar donderdag, en Bram de Vries doet hem. Plan aanpassen?'),
    ).toBeInTheDocument();
  });

  it('applies the suggestion to the plan, without a null for the person', async () => {
    const fetchMock = setup([SUGGESTION], { 'POST /api/v2/promote-suggestions/apply': { plan: {}, warnings: [] } });
    renderWithProviders(<PromoteBanner />);
    fireEvent.click(await screen.findByRole('button', { name: 'Plan aanpassen' }));
    await waitFor(() =>
      expect(bodyOf(fetchMock, '/api/v2/promote-suggestions/apply')).toEqual({ planId: 'p1', taskId: 't1', weekIndex: 1, weekday: 2, toWeekday: 3 }),
    );
    expect(await screen.findByRole('status')).toHaveTextContent('Het plan is aangepast.');
  });

  it('applies a suggestion that also changes the person, with that person in the body', async () => {
    const fetchMock = setup([{ ...SUGGESTION, toAssigneeId: BRAM._id }], { 'POST /api/v2/promote-suggestions/apply': { plan: {}, warnings: [] } });
    renderWithProviders(<PromoteBanner />);
    fireEvent.click(await screen.findByRole('button', { name: 'Plan aanpassen' }));
    await waitFor(() =>
      expect(bodyOf(fetchMock, '/api/v2/promote-suggestions/apply')).toEqual({
        planId: 'p1',
        taskId: 't1',
        weekIndex: 1,
        weekday: 2,
        toWeekday: 3,
        toAssigneeId: BRAM._id,
      }),
    );
  });

  it('dismisses with the newest evidence so it only returns on new moves, with an explicit null for no person', async () => {
    const fetchMock = setup([SUGGESTION], { 'POST /api/v2/promote-suggestions/dismiss': { dismissed: true } });
    renderWithProviders(<PromoteBanner />);
    fireEvent.click(await screen.findByRole('button', { name: 'Nee, laat zo' }));
    await waitFor(() =>
      expect(bodyOf(fetchMock, '/api/v2/promote-suggestions/dismiss')).toEqual({
        planId: 'p1',
        taskId: 't1',
        weekIndex: 1,
        weekday: 2,
        toWeekday: 3,
        toAssigneeId: null,
        lastEvidenceId: 'o2',
      }),
    );
  });

  it('explains when the change does not fit the plan', async () => {
    setup([SUGGESTION], { 'POST /api/v2/promote-suggestions/apply': () => problem(422, 'invalid_plan') });
    renderWithProviders(<PromoteBanner />);
    fireEvent.click(await screen.findByRole('button', { name: 'Plan aanpassen' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Dit past niet in het plan');
  });

  it('renders nothing without suggestions', async () => {
    const fetchMock = setup([]);
    const { container } = renderWithProviders(<PromoteBanner />);
    await waitFor(() => expect(fetchMock.mock.calls.some(([u]) => u === '/api/v2/promote-suggestions')).toBe(true));
    expect(container.querySelector('.promote-banner')).toBeNull();
  });
});
