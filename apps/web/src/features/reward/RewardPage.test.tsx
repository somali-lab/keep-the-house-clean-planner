import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { applyLanguage } from '../../i18n/runtime.ts';
import { ANNA, makeBadge, makeProgress, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { RewardPage } from './RewardPage.tsx';
import { celebrationKey } from './rewardModel.ts';

const WEEK_KEY = celebrationKey(ANNA._id, 'week', '2026-09-14');
const TOILET = makeBadge({ _id: 'b00000000000000000000001', name: 'Toiletjuffrouw' });

type Progress = ReturnType<typeof makeProgress>;

function setup(progress: Progress | ((period: string) => Progress) = makeProgress(), extra: Record<string, unknown> = {}) {
  storeProfile(ANNA._id);
  return mockApi({
    '/api/users': [ANNA],
    '/api/settings': makeSettings(),
    '/api/points/progress': (_init: RequestInit | undefined, url: string) =>
      typeof progress === 'function' ? progress(new URL(url, 'http://localhost').searchParams.get('period') ?? 'week') : progress,
    '/api/badges': { badges: [TOILET] },
    '/api/badges/progress': { personId: ANNA._id, items: [{ badgeId: TOILET._id, current: 10, threshold: 10, awardedAt: '2026-09-16T08:00:00.000Z' }] },
    ...extra,
  });
}

/** Makes the browser report (or not) that the person asked for reduced motion. */
function reduceMotion(reduce: boolean) {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({
      matches: reduce && query.includes('prefers-reduced-motion'),
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  );
}

const scene = () => screen.getByRole('img', { name: /Een kip loopt naar de mand/ });

describe('RewardPage', () => {
  it('states the progress as text, with its money, and exposes it as a progress bar, a picture and eggs', async () => {
    setup(makeProgress({ centsPerPoint: 25, money: { earned: 75, goal: 100 } }));
    renderWithProviders(<RewardPage />);

    expect(await screen.findByText('3 van 4 punten (75%)')).toBeInTheDocument();
    expect(screen.getByText('Waarde: € 0,75 van € 1,00')).toBeInTheDocument();
    expect(screen.getByText('7 van 10 eieren in de mand')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1, name: 'Beloning' })).toBeInTheDocument();
    expect(screen.getByText('14 sep t/m 20 sep')).toBeInTheDocument();

    const bar = screen.getByRole('progressbar', { name: 'Voortgang deze week' });
    expect(bar).toHaveAttribute('aria-valuenow', '75');
    expect(bar).toHaveAttribute('aria-valuemin', '0');
    expect(bar).toHaveAttribute('aria-valuemax', '100');
    expect(bar).toHaveAttribute('aria-valuetext', '3 van 4 punten (75%)');

    // The picture has a text alternative with the eggs, and the chicken walks to 75% of the track with a transform.
    expect(scene()).toHaveAccessibleName('Een kip loopt naar de mand met eieren. 7 van 10 eieren in de mand.');
    const chicken = await screen.findByTestId('reward-chicken');
    await waitFor(() => expect(chicken).toHaveAttribute('data-position', '75'));
    expect(chicken.style.transform).toBe('translateX(142.5px)');
    // 7 eggs are in the basket and 3 places are empty outlines; none of it relies on colour.
    expect(scene().querySelectorAll('.reward-egg')).toHaveLength(7);
    expect(scene().querySelectorAll('ellipse[stroke-dasharray]')).toHaveLength(3);
    expect(screen.queryByText('Doel gehaald!')).not.toBeInTheDocument();
    expect(screen.getByText('Het doel is het totaal van de punten van het werk dat voor je gepland staat.')).toBeInTheDocument();
  });

  it('shows only the earned money when the goal has none, and says when an administrator set the goal', async () => {
    setup(makeProgress({ goalSource: 'explicit', centsPerPoint: 10, money: { earned: 30, goal: 40 } }));
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('Waarde: € 0,30 van € 0,40')).toBeInTheDocument();
    expect(screen.getByText('Het doel is ingesteld door een beheerder.')).toBeInTheDocument();
  });

  it('shows no money when a point is worth nothing', async () => {
    setup();
    renderWithProviders(<RewardPage />);
    await screen.findByText('3 van 4 punten (75%)');
    expect(screen.queryByText(/Waarde:/)).not.toBeInTheDocument();
  });

  it('says there is no goal this period instead of 0 of 0, and shows no meter', async () => {
    setup(makeProgress({ goalPoints: null, percent: 0, earnedPoints: 2 }));
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('Geen doel deze periode.')).toBeInTheDocument();
    expect(screen.getByText('2 punten verdiend')).toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    expect(screen.queryByText(/0 van 0/)).not.toBeInTheDocument();
    expect(screen.queryByRole('img', { name: /Een kip loopt/ })).not.toBeInTheDocument();
  });

  it('switches between the week and the cycle, asks for that period, and remembers the choice per profile', async () => {
    const fetchMock = setup((period) =>
      period === 'cycle'
        ? makeProgress({ period: 'cycle', start: '2026-09-14', end: '2026-10-11', earnedPoints: 5, goalPoints: 20, percent: 25 })
        : makeProgress(),
    );
    const { unmount } = renderWithProviders(<RewardPage />);
    await screen.findByText('3 van 4 punten (75%)');
    const week = screen.getByRole('button', { name: 'Week' });
    const cycle = screen.getByRole('button', { name: 'Cyclus' });
    expect(week).toHaveAttribute('aria-pressed', 'true');
    expect(cycle).toHaveAttribute('aria-pressed', 'false');

    fireEvent.click(cycle);
    expect(await screen.findByText('5 van 20 punten (25%)')).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Voortgang deze cyclus' })).toHaveAttribute('aria-valuenow', '25');
    expect(screen.getByText('14 sep t/m 11 okt')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cyclus' })).toHaveAttribute('aria-pressed', 'true');
    expect(fetchMock.mock.calls.some(([url]) => String(url) === `/api/points/progress?personId=${ANNA._id}&period=cycle`)).toBe(true);
    expect(window.localStorage.getItem(`huishoudplanner.filters.${ANNA._id}.reward.period`)).toBe('"cycle"');

    // The choice is still there after the tab is opened again.
    unmount();
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('5 van 20 punten (25%)')).toBeInTheDocument();
  });

  it('falls back to the week when the stored period is not a period', async () => {
    window.localStorage.setItem(`huishoudplanner.filters.${ANNA._id}.reward.period`, '"year"');
    setup();
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('3 van 4 punten (75%)')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Week' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('shows the earned badges of the profile below the meter', async () => {
    setup();
    renderWithProviders(<RewardPage />);
    const badges = await screen.findByRole('region', { name: 'Mijn badges' });
    expect(within(badges).getByRole('img', { name: 'Toiletjuffrouw' })).toBeInTheDocument();
    expect(within(badges).getByText('1 behaald')).toBeInTheDocument();
    // The badges come after the meter in the page.
    const meter = screen.getByRole('progressbar');
    expect(meter.compareDocumentPosition(badges) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('shows an error when the progress cannot be read', async () => {
    storeProfile(ANNA._id);
    mockApi({ '/api/users': [ANNA], '/api/settings': makeSettings() });
    renderWithProviders(<RewardPage />);
    expect(await screen.findByRole('alert')).toHaveTextContent('De voortgang kon niet worden geladen.');
  });

  it('writes the texts in English', async () => {
    applyLanguage('en');
    setup(makeProgress({ percent: 100, earnedPoints: 4 }));
    reduceMotion(true);
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('4 of 4 points (100%)')).toBeInTheDocument();
    expect(screen.getByText('10 of 10 eggs in the basket')).toBeInTheDocument();
    expect(screen.getByText('Goal reached!')).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Progress this week' })).toHaveAttribute('aria-valuenow', '100');
  });

  describe('the completion animation', () => {
    it('plays once when the meter is full, is remembered, and does not play again after a reload', async () => {
      reduceMotion(false);
      setup(makeProgress({ percent: 100, earnedPoints: 4 }));
      const { unmount } = renderWithProviders(<RewardPage />);

      expect(await screen.findByText('4 van 4 punten (100%)')).toBeInTheDocument();
      await waitFor(() => expect(scene()).toHaveAttribute('data-celebrating', 'true'));
      expect(scene()).toHaveClass('reward-celebrating');
      expect(scene().querySelectorAll('.reward-egg')).toHaveLength(10);
      expect(screen.getByText('Doel gehaald!')).toBeInTheDocument();
      expect(window.localStorage.getItem(WEEK_KEY)).toBe('1');

      // Opening the tab again (a reload or another tab) never replays it; the text still says the goal is met.
      unmount();
      renderWithProviders(<RewardPage />);
      expect(await screen.findByText('4 van 4 punten (100%)')).toBeInTheDocument();
      expect(scene()).toHaveAttribute('data-celebrating', 'false');
      expect(scene()).not.toHaveClass('reward-celebrating');
      expect(screen.getByText('Doel gehaald!')).toBeInTheDocument();
    });

    it('does not play when it was remembered for this person and period', async () => {
      reduceMotion(false);
      window.localStorage.setItem(WEEK_KEY, '1');
      setup(makeProgress({ percent: 100, earnedPoints: 4 }));
      renderWithProviders(<RewardPage />);
      expect(await screen.findByText('Doel gehaald!')).toBeInTheDocument();
      expect(scene()).toHaveAttribute('data-celebrating', 'false');
    });

    it('plays again for another period, and the cycle is celebrated separately from the week', async () => {
      reduceMotion(false);
      window.localStorage.setItem(WEEK_KEY, '1');
      setup((period) => makeProgress({ period: period === 'cycle' ? 'cycle' : 'week', percent: 100, earnedPoints: 4 }));
      renderWithProviders(<RewardPage />);
      await screen.findByText('Doel gehaald!');
      expect(scene()).toHaveAttribute('data-celebrating', 'false');

      fireEvent.click(screen.getByRole('button', { name: 'Cyclus' }));
      await waitFor(() => expect(scene()).toHaveAttribute('data-celebrating', 'true'));
      expect(window.localStorage.getItem(celebrationKey(ANNA._id, 'cycle', '2026-09-14'))).toBe('1');
    });

    it('plays when the meter becomes full while the tab is open', async () => {
      reduceMotion(false);
      let progress = makeProgress({ percent: 75 });
      const fetchMock = setup(() => progress);
      const { queryClient } = renderWithProviders(<RewardPage />);
      await screen.findByText('3 van 4 punten (75%)');
      expect(scene()).toHaveAttribute('data-celebrating', 'false');
      expect(window.localStorage.getItem(WEEK_KEY)).toBeNull();

      progress = makeProgress({ percent: 100, earnedPoints: 4 });
      await queryClient.invalidateQueries({ queryKey: ['points'] });
      await waitFor(() => expect(scene()).toHaveAttribute('data-celebrating', 'true'));
      expect(fetchMock).toHaveBeenCalled();
    });

    it('is never played with reduced motion: the goal is announced as a static message and nothing is remembered', async () => {
      reduceMotion(true);
      setup(makeProgress({ percent: 100, earnedPoints: 4 }));
      renderWithProviders(<RewardPage />);

      const message = await screen.findByText('Doel gehaald!');
      expect(message.closest('[role="status"]')).not.toBeNull();
      expect(scene()).toHaveAttribute('data-celebrating', 'false');
      expect(scene()).not.toHaveClass('reward-celebrating');
      expect(window.localStorage.getItem(WEEK_KEY)).toBeNull();
      // The chicken is already in place: it does not walk there.
      expect(screen.getByTestId('reward-chicken')).toHaveAttribute('data-position', '100');
      expect(scene()).not.toHaveClass('reward-walking');
      expect(screen.getByText('4 van 4 punten (100%)')).toBeInTheDocument();
    });

    it('plays nothing and says nothing while the goal is not met', async () => {
      reduceMotion(false);
      setup(makeProgress({ percent: 99, earnedPoints: 3, goalPoints: 4 }));
      renderWithProviders(<RewardPage />);
      await screen.findByText('3 van 4 punten (99%)');
      expect(scene()).toHaveAttribute('data-celebrating', 'false');
      expect(screen.queryByText('Doel gehaald!')).not.toBeInTheDocument();
      expect(window.localStorage.length).toBe(1); // only the profile id
    });
  });
});
