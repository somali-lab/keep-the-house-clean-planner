import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, LIMITS, householdRoutes, mockApi, problem, requestsTo, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { parseRewardGoal, RewardGoalsSection } from './RewardGoalsSection.tsx';

function setup(profileId: string, settings = makeSettings(), patch: unknown = { ...settings, version: 2 }) {
  storeProfile(profileId);
  return mockApi({
    ...householdRoutes([ANNA, BRAM], settings),
    '/api/v2/meta/limits': LIMITS,
    'PATCH /api/v2/settings': patch,
  });
}

const patchBodies = (fetchMock: ReturnType<typeof mockApi>) =>
  requestsTo(fetchMock, 'PATCH', '/api/v2/settings').map((request) => request.body);

describe('parseRewardGoal', () => {
  it.each([
    ['', null],
    ['  ', null],
    ['0', 0],
    ['12', 12],
    [' 100000 ', 100000],
    ['100001', undefined],
    ['-1', undefined],
    ['2.5', undefined],
    ['abc', undefined],
    ['1234567', undefined],
  ])('%j gives %s', (text, expected) => {
    expect(parseRewardGoal(text)).toBe(expected);
  });
});

describe('RewardGoalsSection', () => {
  it('shows nothing to anyone but an administrator', async () => {
    setup(BRAM.id);
    const { container } = renderWithProviders(<RewardGoalsSection settings={makeSettings()} />);
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Beloningsdoelen' })).not.toBeInTheDocument());
    expect(container).toBeEmptyDOMElement();
  });

  it('shows the goals in force, and an empty field for an automatic goal', async () => {
    setup(ANNA.id);
    renderWithProviders(<RewardGoalsSection settings={makeSettings({ rewardGoals: { weekPoints: 12, cyclePoints: null } })} />);
    const form = await screen.findByRole('form', { name: 'Beloningsdoelen' });
    expect(within(form).getByLabelText('Doel per week (punten)')).toHaveValue(12);
    expect(within(form).getByLabelText('Doel per cyclus (punten)')).toHaveValue(null);
    expect(within(form).getByLabelText('Doel per cyclus (punten)')).toHaveAttribute('placeholder', 'Automatisch');
  });

  it('saves both goals together, with an empty field as null, and confirms it', async () => {
    const fetchMock = setup(ANNA.id);
    renderWithProviders(<RewardGoalsSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Beloningsdoelen' });
    fireEvent.change(within(form).getByLabelText('Doel per week (punten)'), { target: { value: '15' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Doelen opslaan' }));
    await waitFor(() => expect(patchBodies(fetchMock)).toEqual([{ rewardGoals: { weekPoints: 15, cyclePoints: null } }]));
    expect(await within(form).findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('sends the version of the settings as If-Match', async () => {
    const settings = makeSettings({ version: 9 });
    const fetchMock = setup(ANNA.id, settings);
    renderWithProviders(<RewardGoalsSection settings={settings} />);
    const form = await screen.findByRole('form', { name: 'Beloningsdoelen' });
    fireEvent.click(within(form).getByRole('button', { name: 'Doelen opslaan' }));
    await waitFor(() => expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')).toHaveLength(1));
    expect(requestsTo(fetchMock, 'PATCH', '/api/v2/settings')[0]!.headers['if-match']).toBe('"9"');
  });

  it('keeps the typed goal and says so when the settings changed in the meantime (412)', async () => {
    setup(ANNA.id, makeSettings(), problem(412, 'precondition_failed', 'Changed.'));
    renderWithProviders(<RewardGoalsSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Beloningsdoelen' });
    fireEvent.change(within(form).getByLabelText('Doel per week (punten)'), { target: { value: '30' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Doelen opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Deze gegevens zijn intussen door iemand anders gewijzigd');
    expect(within(form).getByLabelText('Doel per week (punten)')).toHaveValue(30);
  });

  it('can switch a goal off with 0', async () => {
    const fetchMock = setup(ANNA.id);
    renderWithProviders(<RewardGoalsSection settings={makeSettings({ rewardGoals: { weekPoints: 5, cyclePoints: 20 } })} />);
    const form = await screen.findByRole('form', { name: 'Beloningsdoelen' });
    fireEvent.change(within(form).getByLabelText('Doel per cyclus (punten)'), { target: { value: '0' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Doelen opslaan' }));
    await waitFor(() => expect(patchBodies(fetchMock)).toEqual([{ rewardGoals: { weekPoints: 5, cyclePoints: 0 } }]));
  });

  it('refuses a goal outside 0 to 100000 or that is not a whole number, without calling the server', async () => {
    const fetchMock = setup(ANNA.id);
    renderWithProviders(<RewardGoalsSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Beloningsdoelen' });
    for (const bad of ['100001', '-1', '2.5']) {
      fireEvent.change(within(form).getByLabelText('Doel per week (punten)'), { target: { value: bad } });
      fireEvent.click(within(form).getByRole('button', { name: 'Doelen opslaan' }));
      expect(within(form).getByRole('alert')).toHaveTextContent('Een doel moet een heel getal van 0 tot 100000 zijn, of leeg.');
    }
    expect(patchBodies(fetchMock)).toEqual([]);
  });

  it('shows an error when the server refuses', async () => {
    setup(ANNA.id, makeSettings(), problem(403, 'permission_denied', 'Not allowed.'));
    renderWithProviders(<RewardGoalsSection settings={makeSettings()} />);
    const form = await screen.findByRole('form', { name: 'Beloningsdoelen' });
    fireEvent.click(within(form).getByRole('button', { name: 'Doelen opslaan' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Er ging iets mis.');
  });
});
