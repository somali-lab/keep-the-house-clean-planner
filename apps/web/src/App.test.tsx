import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { App } from './App.tsx';
import { APP_VERSION } from './version.ts';
import { ANNA, BRAM, makeProgress, mockApi, page, storeProfile, testQueryClient, v2Basics } from './test/fixtures.ts';
import { makeSettings } from './test/render.tsx';
import { setViewportWidth } from './test/setup.ts';

describe('app shell', () => {
  beforeEach(() => {
    window.history.replaceState(null, '', '/');
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/v2/tasks': page([]),
      '/api/v2/rooms': page([]),
      '/api/settings': makeSettings(),
      '/api/cycle-plans': [],
      '/api/occurrences': [],
      ...v2Basics(),
      '/api/v2/occurrences': page([]),
    });
    storeProfile(ANNA._id);
  });

  it('renders the standard overview on narrow screens', async () => {
    setViewportWidth(375);
    render(<App queryClient={testQueryClient()} />);
    const nav = await screen.findByRole('navigation', { name: 'Hoofdmenu' });
    expect(nav).toHaveTextContent('Vandaag');
    expect(nav).toHaveTextContent('Taken');
    expect(nav).toHaveTextContent('Achterstand');
    expect(nav).not.toHaveTextContent('Planner');
    expect(screen.getByRole('group', { name: 'Kleurthema' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Taal' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Instellingen en beheer openen' })).toBeInTheDocument();
    expect(screen.getByLabelText(`Versie ${APP_VERSION}`)).toHaveTextContent(`v${APP_VERSION}`);
    expect(await screen.findByRole('heading', { name: 'Weekoverzicht' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/');
  });

  it('puts the filter reset directly left of the language switch in the overview header', async () => {
    setViewportWidth(375);
    render(<App queryClient={testQueryClient()} />);
    const reset = await screen.findByRole('button', { name: 'Filters van dit scherm resetten' });
    const language = screen.getByRole('group', { name: 'Taal' });
    expect(reset.closest('header')).toBe(language.closest('header'));
    // Only the (visually hidden) status region sits between the button and the language switch.
    expect(reset.nextElementSibling?.nextElementSibling).toBe(language);
    // The overview screen with default filters has nothing to reset yet.
    expect(reset).toBeDisabled();
  });

  it.each([320, 375])('lists the five overview tabs, with the reward tab, in a keyboard accessible menu at %i px', async (width) => {
    setViewportWidth(width);
    render(<App queryClient={testQueryClient()} />);
    const nav = await screen.findByRole('navigation', { name: 'Hoofdmenu' });
    const links = within(nav).getAllByRole('link');
    expect(links.map((link) => link.textContent)).toEqual(['Week', 'Vandaag', 'Taken', 'Achterstand', 'Beloning']);
    expect(links.map((link) => link.getAttribute('href'))).toEqual(['/', '/today', '/tasks', '/due', '/reward']);
    // Five equal columns that may shrink below their text, so the longest label wraps instead of pushing the menu wider.
    expect(links[0]!.parentElement).toHaveClass('grid-cols-5');
    for (const link of links) {
      expect(link).toHaveClass('min-w-0');
      expect(link).not.toHaveAttribute('tabindex', '-1');
      link.focus();
      expect(link).toHaveFocus();
    }
    // Each tab is an icon and a word, so none relies on the icon alone.
    for (const link of links) expect(link.querySelector('svg')).toHaveAttribute('aria-hidden', 'true');
  });

  it('opens the reward tab from the menu, for the active profile', async () => {
    setViewportWidth(375);
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/v2/tasks': page([]),
      '/api/v2/rooms': page([]),
      '/api/settings': makeSettings(),
      '/api/cycle-plans': [],
      '/api/occurrences': [],
      ...v2Basics(),
      '/api/v2/occurrences': page([]),
      '/api/points/progress': makeProgress({ personId: ANNA._id }),
      '/api/badges': { badges: [] },
      '/api/badges/progress': { personId: ANNA._id, items: [] },
    });
    render(<App queryClient={testQueryClient()} />);
    fireEvent.click(await screen.findByRole('link', { name: 'Beloning' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Beloning' })).toBeInTheDocument();
    expect(await screen.findByText('3 van 4 punten (75%)')).toBeInTheDocument();
    expect(window.location.pathname).toBe('/reward');
    expect(screen.getByRole('link', { name: 'Beloning' })).toHaveAttribute('aria-current', 'page');
  });

  it('makes the focused standard view directly accessible on wide screens', async () => {
    setViewportWidth(1280);
    window.history.replaceState(null, '', '/today');
    render(<App queryClient={testQueryClient()} />);

    const nav = await screen.findByRole('navigation', { name: 'Hoofdmenu' });
    expect(nav).toHaveTextContent('Vandaag');
    expect(nav).not.toHaveTextContent('Planner');
    expect(screen.getByRole('link', { name: 'Achterstand' })).toHaveAttribute(
      'href',
      '/due',
    );
    expect(screen.getByRole('link', { name: 'Taken' })).toHaveAttribute(
      'href',
      '/tasks',
    );
  });

  it('renders the management view when its route is opened', async () => {
    setViewportWidth(1280);
    window.history.replaceState(null, '', '/manage/planner');
    render(<App queryClient={testQueryClient()} />);
    const nav = await screen.findByRole('navigation', { name: 'Hoofdmenu' });
    expect(nav).toHaveTextContent('Planner');
    expect(nav).toHaveTextContent('Verdeling');
    expect(nav).not.toHaveTextContent('AI-assistent');
    expect(nav).not.toHaveTextContent('AI-prompts');
    expect(nav).not.toHaveTextContent('Week');
    expect(nav).toHaveTextContent('Instellingen');
    expect(nav).toHaveTextContent('Gereedmeldingen');
    expect(nav).toHaveTextContent('Badges');
    expect(screen.getByRole('link', { name: 'Taken' })).toHaveAttribute('href', '/manage/tasks');
    expect(screen.getByRole('button', { name: 'Terug naar overzicht' })).toHaveClass('cursor-pointer');
    expect(screen.getByRole('group', { name: 'Kleurthema' })).toBeInTheDocument();
    const version = screen.getByLabelText(`Versie ${APP_VERSION}`);
    expect(version).toHaveTextContent(`v${APP_VERSION}`);
    fireEvent.click(screen.getByRole('button', { name: 'Menu inklappen' }));
    expect(version.closest('.visually-hidden')).toBeNull();
    expect(screen.getByRole('link', { name: 'Planner' })).toHaveAttribute('aria-current', 'page');
  });

  it('shows household members only the read and execution sections', async () => {
    setViewportWidth(1280);
    const member = { ...BRAM, role: 'member' as const };
    mockApi({
      '/api/users': [ANNA, member],
      '/api/v2/tasks': page([]),
      '/api/v2/rooms': page([]),
      '/api/settings': makeSettings(),
      '/api/cycle-plans': [],
      '/api/occurrences': [],
      ...v2Basics(),
      '/api/v2/occurrences': page([]),
    });
    storeProfile(member._id);
    window.history.replaceState(null, '', '/manage/distribution');
    render(<App queryClient={testQueryClient()} />);

    const nav = await screen.findByRole('navigation', { name: 'Hoofdmenu' });
    expect(nav).not.toHaveTextContent('Week');
    expect(nav).toHaveTextContent('Verdeling');
    expect(nav).toHaveTextContent('Statistiek');
    expect(nav).not.toHaveTextContent('Planner');
    expect(nav).not.toHaveTextContent('Taken');
    expect(nav).not.toHaveTextContent('Instellingen');
    expect(nav).not.toHaveTextContent('Gereedmeldingen');
    expect(nav).not.toHaveTextContent('Badges');
  });

  it('opens management with the gear and returns to the standard overview', async () => {
    setViewportWidth(375);
    render(<App queryClient={testQueryClient()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Instellingen en beheer openen' }));
    await waitFor(() => expect(window.location.pathname).toBe('/manage/planner'));
    expect(screen.getByRole('link', { name: 'Planner' })).toHaveAttribute('aria-current', 'page');

    fireEvent.click(screen.getByRole('button', { name: 'Terug naar overzicht' }));
    expect(await screen.findByRole('heading', { name: 'Weekoverzicht' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/');
  });

  it('puts the filter reset directly left of the language switch in the management header', async () => {
    setViewportWidth(1280);
    window.history.replaceState(null, '', '/manage/settings');
    render(<App queryClient={testQueryClient()} />);
    const reset = await screen.findByRole('button', { name: 'Filters van dit scherm resetten' });
    const language = screen.getByRole('group', { name: 'Taal' });
    expect(reset.closest('header')).toBe(language.closest('header'));
    expect(reset.nextElementSibling?.nextElementSibling).toBe(language);
    // A screen without filters never enables the button.
    expect(reset).toBeDisabled();
  });

  it('resets only the filters of the screen on display and names the button in English', async () => {
    setViewportWidth(1280);
    const key = (name: string) => `huishoudplanner.filters.${ANNA._id}.${name}`;
    window.localStorage.setItem(key('tasks.room'), '"r1"');
    window.localStorage.setItem(key('stats.period'), '{"unit":"cycles","count":4}');
    window.history.replaceState(null, '', '/manage/statistics');
    render(<App queryClient={testQueryClient()} />);

    const reset = await screen.findByRole('button', { name: 'Filters van dit scherm resetten' });
    await waitFor(() => expect(reset).toBeEnabled());
    fireEvent.click(reset);
    await waitFor(() => expect(reset).toBeDisabled());
    expect(window.localStorage.getItem(key('stats.period'))).toBeNull();
    expect(window.localStorage.getItem(key('tasks.room'))).toBe('"r1"');
    expect(screen.getByText('Filters gereset')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Engels' }));
    expect(await screen.findByRole('button', { name: "Reset This Screen's Filters" })).toBeDisabled();
  });

  it('returns from any management page to the week overview with the Home button', async () => {
    setViewportWidth(1280);
    window.history.replaceState(null, '', '/manage/statistics');
    render(<App queryClient={testQueryClient()} />);

    const home = await screen.findByRole('button', { name: 'Home: naar het weekoverzicht' });
    expect(screen.getByRole('navigation', { name: 'Hoofdmenu' })).toHaveTextContent('Statistiek');
    fireEvent.click(home);
    expect(await screen.findByRole('heading', { name: 'Weekoverzicht' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/');
  });

  it('switches the interface to English immediately and remembers that choice', async () => {
    setViewportWidth(1280);
    window.history.replaceState(null, '', '/manage/planner');
    render(<App queryClient={testQueryClient()} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Engels' }));

    const nav = await screen.findByRole('navigation', { name: 'Main Menu' });
    expect(nav).toHaveTextContent('Planner');
    expect(nav).toHaveTextContent('Distribution');
    expect(nav).toHaveTextContent('Statistics');
    expect(screen.getByText(ANNA.name)).toBeInTheDocument();
    expect(window.localStorage.getItem('huishoudplanner.language')).toBe('en');
    expect(document.documentElement.lang).toBe('en');
  });
});
