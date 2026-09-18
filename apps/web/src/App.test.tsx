import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { App } from './App.tsx';
import { APP_VERSION } from './version.ts';
import { ANNA, BRAM, mockApi, storeProfile, testQueryClient } from './test/fixtures.ts';
import { makeSettings } from './test/render.tsx';
import { setViewportWidth } from './test/setup.ts';

describe('app shell', () => {
  beforeEach(() => {
    window.history.replaceState(null, '', '/');
    mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/tasks': [],
      '/api/rooms': [],
      '/api/settings': makeSettings(),
      '/api/cycle-plans': [],
      '/api/occurrences': [],
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
    expect(window.location.pathname).toBe('/mobile/week');
  });

  it('makes the focused standard view directly accessible on wide screens', async () => {
    setViewportWidth(1280);
    window.history.replaceState(null, '', '/mobile/today');
    render(<App queryClient={testQueryClient()} />);

    const nav = await screen.findByRole('navigation', { name: 'Hoofdmenu' });
    expect(nav).toHaveTextContent('Vandaag');
    expect(nav).not.toHaveTextContent('Planner');
    expect(screen.getByRole('link', { name: 'Achterstand' })).toHaveAttribute(
      'href',
      '/mobile/due',
    );
    expect(screen.getByRole('link', { name: 'Taken' })).toHaveAttribute(
      'href',
      '/mobile/tasks',
    );
  });

  it('renders the management view when its route is opened', async () => {
    setViewportWidth(1280);
    window.history.replaceState(null, '', '/planner');
    render(<App queryClient={testQueryClient()} />);
    const nav = await screen.findByRole('navigation', { name: 'Hoofdmenu' });
    expect(nav).toHaveTextContent('Planner');
    expect(nav).toHaveTextContent('Verdeling');
    expect(nav).not.toHaveTextContent('AI-assistent');
    expect(nav).not.toHaveTextContent('AI-prompts');
    expect(nav).not.toHaveTextContent('Week');
    expect(nav).toHaveTextContent('Instellingen');
    expect(screen.getByRole('button', { name: 'Terug naar overzicht' })).toBeInTheDocument();
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
      '/api/tasks': [],
      '/api/rooms': [],
      '/api/settings': makeSettings(),
      '/api/cycle-plans': [],
      '/api/occurrences': [],
    });
    storeProfile(member._id);
    window.history.replaceState(null, '', '/distribution');
    render(<App queryClient={testQueryClient()} />);

    const nav = await screen.findByRole('navigation', { name: 'Hoofdmenu' });
    expect(nav).not.toHaveTextContent('Week');
    expect(nav).toHaveTextContent('Verdeling');
    expect(nav).toHaveTextContent('Statistiek');
    expect(nav).not.toHaveTextContent('Planner');
    expect(nav).not.toHaveTextContent('Taken');
    expect(nav).not.toHaveTextContent('Instellingen');
  });

  it('opens management with the gear and returns to the standard overview', async () => {
    setViewportWidth(375);
    render(<App queryClient={testQueryClient()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Instellingen en beheer openen' }));
    await waitFor(() => expect(window.location.pathname).toBe('/planner'));
    expect(screen.getByRole('link', { name: 'Planner' })).toHaveAttribute('aria-current', 'page');

    fireEvent.click(screen.getByRole('button', { name: 'Terug naar overzicht' }));
    expect(await screen.findByRole('heading', { name: 'Weekoverzicht' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/mobile/week');
  });

  it('redirects old Dutch URLs to their English replacements', async () => {
    setViewportWidth(1280);
    window.history.replaceState(null, '', '/taken?source=bookmark');
    render(<App queryClient={testQueryClient()} />);

    await screen.findByRole('heading', { name: 'Taken' });
    await waitFor(() => expect(window.location.pathname).toBe('/tasks'));
    expect(window.location.search).toBe('?source=bookmark');
  });

  it('switches the interface to English immediately and remembers that choice', async () => {
    setViewportWidth(1280);
    window.history.replaceState(null, '', '/planner');
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
