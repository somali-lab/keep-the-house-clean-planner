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

  it('renders the mobile layout on narrow screens', async () => {
    setViewportWidth(375);
    render(<App queryClient={testQueryClient()} />);
    const nav = await screen.findByRole('navigation', { name: 'Hoofdmenu' });
    expect(nav).toHaveTextContent('Vandaag');
    expect(nav).toHaveTextContent('Taken');
    expect(nav).toHaveTextContent('Achterstand');
    expect(nav).not.toHaveTextContent('Planner');
    expect(screen.getByRole('group', { name: 'Kleurthema' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Taal' })).toBeInTheDocument();
    expect(screen.getByLabelText(`Versie ${APP_VERSION}`)).toHaveTextContent(`v${APP_VERSION}`);
    expect(await screen.findByRole('heading', { name: '12-daags overzicht' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/mobile/week');
  });

  it('makes the focused mobile view directly accessible on wide screens', async () => {
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

  it('renders the desktop layout on wide screens', async () => {
    setViewportWidth(1280);
    render(<App queryClient={testQueryClient()} />);
    const nav = await screen.findByRole('navigation', { name: 'Hoofdmenu' });
    expect(nav).toHaveTextContent('Planner');
    expect(nav).toHaveTextContent('Verdeling');
    expect(nav).toHaveTextContent('AI-prompts');
    expect(nav).toHaveTextContent('Instellingen');
    expect(screen.getByRole('group', { name: 'Kleurthema' })).toBeInTheDocument();
    const version = screen.getByLabelText(`Versie ${APP_VERSION}`);
    expect(version).toHaveTextContent(`v${APP_VERSION}`);
    fireEvent.click(screen.getByRole('button', { name: 'Menu inklappen' }));
    expect(version.closest('.visually-hidden')).toBeNull();
    expect(await screen.findByRole('heading', { name: '12-daags overzicht' })).toBeInTheDocument();
  });

  it('can switch to the other layout via the menu', async () => {
    setViewportWidth(375);
    render(<App queryClient={testQueryClient()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Naar planweergave (desktop)' }));
    expect(await screen.findByRole('heading', { name: '12-daags overzicht' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/week');
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
