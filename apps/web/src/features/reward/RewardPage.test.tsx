import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { applyLanguage } from '../../i18n/runtime.ts';
import { ANNA, makeBadge, makeProgress, mockApi, page, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { PROGRESS_REFETCH_MS } from './api.ts';
import { RewardPage } from './RewardPage.tsx';
import { celebrationKey } from './rewardModel.ts';

const WEEK_KEY = celebrationKey(ANNA.id, 'week', '2026-09-14');
const TOILET = makeBadge({ id: 'b00000000000000000000001', name: 'Toiletjuffrouw' });

type Progress = ReturnType<typeof makeProgress>;
const progressCalls = (fetchMock: ReturnType<typeof mockApi>) => fetchMock.mock.calls.filter(([url]) => String(url).startsWith('/api/v2/points/progress'));

function setup(progress: Progress | ((period: string) => Progress) = makeProgress(), extra: Record<string, unknown> = {}) {
  storeProfile(ANNA.id);
  return mockApi({
    '/api/v2/users': page([ANNA]),
    '/api/v2/settings': makeSettings(),
    '/api/v2/points/progress': (_init: RequestInit | undefined, url: string) =>
      typeof progress === 'function' ? progress(new URL(url, 'http://localhost').searchParams.get('period') ?? 'week') : progress,
    '/api/v2/badges': page([TOILET]),
    '/api/v2/badges/progress': { personId: ANNA.id, items: [{ badgeId: TOILET.id, current: 10, threshold: 10, awardedAt: '2026-09-16T08:00:00.000Z' }] },
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

  it('takes the eggs from the server and does not work them out from the percentage', async () => {
    setup(makeProgress({ percent: 75, eggs: 4, eggCount: 10 }));
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('4 van 10 eieren in de mand')).toBeInTheDocument();
    expect(scene().querySelectorAll('.reward-egg')).toHaveLength(4);
    expect(scene().querySelectorAll('ellipse[stroke-dasharray]')).toHaveLength(6);
  });

  it('draws as many places in the basket as the server counts eggs for a full meter', async () => {
    setup(makeProgress({ percent: 100, earnedPoints: 4, eggs: 5, eggCount: 5 }));
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('5 van 5 eieren in de mand')).toBeInTheDocument();
    expect(scene().querySelectorAll('.reward-egg')).toHaveLength(5);
    expect(scene().querySelectorAll('ellipse[stroke-dasharray]')).toHaveLength(0);
  });

  it('formats the money, delivered in cents, with the currency of the household in the language of the interface', async () => {
    setup(makeProgress({ centsPerPoint: 25, currencyCode: 'EUR', money: { earned: 123456, goal: null } }));
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText(/Waarde: € 1.234,56/)).toBeInTheDocument();
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

  it('says an administrator switched the goal off when the goal is explicit and 0, in every period', async () => {
    setup((period) => makeProgress({ period: period === 'cycle' ? 'cycle' : 'week', goalPoints: null, goalSource: 'explicit', percent: 0, earnedPoints: 2 }));
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('De beheerder heeft het doel voor deze periode uitgezet.')).toBeInTheDocument();
    expect(screen.queryByText(/Er staat in deze periode niets/)).not.toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cyclus' }));
    expect(await screen.findByRole('region', { name: 'Voortgang deze cyclus' })).toHaveTextContent('De beheerder heeft het doel voor deze periode uitgezet.');
  });

  it('words the automatic no-goal text so it fits both the week and the cycle', async () => {
    setup(makeProgress({ goalPoints: null, percent: 0, earnedPoints: 0 }));
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('Er staat in deze periode niets voor je gepland en er is geen doel ingesteld.')).toBeInTheDocument();
    expect(screen.queryByText(/De beheerder heeft/)).not.toBeInTheDocument();
  });

  it('writes the switched-off goal in English', async () => {
    applyLanguage('en');
    setup(makeProgress({ goalPoints: null, goalSource: 'explicit', percent: 0, earnedPoints: 0 }));
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('The administrator switched the goal off for this period.')).toBeInTheDocument();
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
    expect(fetchMock.mock.calls.some(([url]) => String(url) === `/api/v2/points/progress?personId=${ANNA.id}&period=cycle`)).toBe(true);
    expect(window.localStorage.getItem(`huishoudplanner.filters.${ANNA.id}.reward.period`)).toBe('"cycle"');

    // The choice is still there after the tab is opened again.
    unmount();
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('5 van 20 punten (25%)')).toBeInTheDocument();
  });

  it('falls back to the week when the stored period is not a period', async () => {
    window.localStorage.setItem(`huishoudplanner.filters.${ANNA.id}.reward.period`, '"year"');
    setup();
    renderWithProviders(<RewardPage />);
    expect(await screen.findByText('3 van 4 punten (75%)')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Week' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('reads the progress again when the week or cycle rolled over while the tab stayed open', async () => {
    const fetchMock = setup(makeProgress());
    // The device says it is already Monday 21 September, but the progress is for 14 to 20 September.
    renderWithProviders(<RewardPage now={new Date('2026-09-21T10:00:00Z')} />);
    await screen.findByText('3 van 4 punten (75%)');
    await waitFor(() => expect(progressCalls(fetchMock).length).toBeGreaterThanOrEqual(2));
  });

  it('does not read again while the day is inside the period that was read', async () => {
    const fetchMock = setup(makeProgress());
    renderWithProviders(<RewardPage now={new Date('2026-09-20T20:00:00Z')} />);
    await screen.findByText('3 van 4 punten (75%)');
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(progressCalls(fetchMock)).toHaveLength(1);
  });

  it('refreshes on every window focus and every five minutes while the tab is open', async () => {
    setup();
    const { queryClient } = renderWithProviders(<RewardPage />);
    await screen.findByText('3 van 4 punten (75%)');
    const query = queryClient.getQueryCache().find({ queryKey: ['points', 'progress', ANNA.id, 'week'] });
    expect(query?.observers[0]?.options.refetchOnWindowFocus).toBe('always');
    expect(query?.observers[0]?.options.refetchInterval).toBe(PROGRESS_REFETCH_MS);
    expect(PROGRESS_REFETCH_MS).toBe(300_000);
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
    storeProfile(ANNA.id);
    mockApi({ '/api/v2/users': page([ANNA]), '/api/v2/settings': makeSettings() });
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
      expect(window.localStorage.getItem(celebrationKey(ANNA.id, 'cycle', '2026-09-14'))).toBe('1');
    });

    it('stops when the last egg has landed, so undoing and redoing the last task does not play it again', async () => {
      reduceMotion(false);
      let progress = makeProgress({ percent: 100, earnedPoints: 4 });
      setup(() => progress);
      const { queryClient } = renderWithProviders(<RewardPage />);
      await waitFor(() => expect(scene()).toHaveAttribute('data-celebrating', 'true'));

      // The last egg lands: the animation is over, the message stays.
      // Fire on the current last egg each attempt: the listener is attached after render and
      // the eggs can be re-rendered, so a single early event could miss it on a slow runner.
      await waitFor(() => {
        const eggs = scene().querySelectorAll('.reward-egg');
        fireEvent(eggs[eggs.length - 1]!, new Event('animationend', { bubbles: true }));
        expect(scene()).toHaveAttribute('data-celebrating', 'false');
      });
      expect(screen.getByText('Doel gehaald!')).toBeInTheDocument();

      // The last task is undone and done again: the meter is full again, and nothing plays.
      progress = makeProgress({ percent: 75 });
      await queryClient.invalidateQueries({ queryKey: ['points'] });
      await screen.findByText('3 van 4 punten (75%)');
      progress = makeProgress({ percent: 100, earnedPoints: 4 });
      await queryClient.invalidateQueries({ queryKey: ['points'] });
      await screen.findByText('4 van 4 punten (100%)');
      expect(scene()).toHaveAttribute('data-celebrating', 'false');
      expect(scene()).not.toHaveClass('reward-celebrating');
    });

    it('stays once-only when storage is blocked: toggling the period and back does not play it again', async () => {
      reduceMotion(false);
      const original = Storage.prototype.getItem;
      setup((period) => (period === 'cycle' ? makeProgress({ period: 'cycle', percent: 50, earnedPoints: 2 }) : makeProgress({ percent: 100, earnedPoints: 4 })));
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key: string) {
        if (key.startsWith('khc.')) throw new Error('storage blocked');
        return original.call(this, key);
      });
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, key: string) {
        if (key.startsWith('khc.')) throw new Error('storage blocked');
      });
      renderWithProviders(<RewardPage />);
      await waitFor(() => expect(scene()).toHaveAttribute('data-celebrating', 'true'));

      fireEvent.click(screen.getByRole('button', { name: 'Cyclus' }));
      await screen.findByText('2 van 4 punten (50%)');
      fireEvent.click(screen.getByRole('button', { name: 'Week' }));
      await screen.findByText('4 van 4 punten (100%)');
      expect(scene()).toHaveAttribute('data-celebrating', 'false');
      expect(screen.getByText('Doel gehaald!')).toBeInTheDocument();
    });

    it('announces the goal in a live region that is in the page before the text appears', async () => {
      reduceMotion(false);
      let progress = makeProgress({ percent: 75 });
      setup(() => progress);
      const { queryClient } = renderWithProviders(<RewardPage />);
      const region = await screen.findByRole('region', { name: 'Voortgang deze week' });
      const status = within(region).getByRole('status');
      expect(status).toBeEmptyDOMElement();

      progress = makeProgress({ percent: 100, earnedPoints: 4 });
      await queryClient.invalidateQueries({ queryKey: ['points'] });
      await waitFor(() => expect(status).toHaveTextContent('Doel gehaald!'));
      // The very same node got the text: it was not inserted with its text already in it.
      expect(within(region).getByRole('status')).toBe(status);
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
