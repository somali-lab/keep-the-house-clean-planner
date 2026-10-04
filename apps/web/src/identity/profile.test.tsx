import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../App.tsx';
import { api, apiV2, unwrap } from '../api/index.ts';
import { ANNA, BRAM, makeUser, mockApi, storeProfile, testQueryClient, page } from '../test/fixtures.ts';
import { setViewportWidth } from '../test/setup.ts';
import { initials } from './Avatar.tsx';
import { getActiveProfileId, resetProfileStore } from './profileStore.ts';

function renderApp() {
  return render(<App queryClient={testQueryClient()} />);
}

describe('profile selection', () => {
  beforeEach(() => {
    window.history.replaceState(null, '', '/');
    setViewportWidth(375);
  });

  it('shows the fullscreen picker without a stored profile, and remembers the choice', async () => {
    mockApi({ '/api/v2/users': page([ANNA, BRAM]) });
    renderApp();

    expect(await screen.findByRole('heading', { name: 'Wie ben jij?' })).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Hoofdmenu' })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Bram de Vries/ }));

    expect(await screen.findByTestId('current-profile')).toHaveTextContent('Bram de Vries');
    expect(screen.getByRole('navigation', { name: 'Hoofdmenu' })).toBeInTheDocument();
    expect(window.localStorage.getItem('huishoudplanner.profileId')).toBe(BRAM.id);
  });

  it('switches profile with one tap in the header', async () => {
    mockApi({ '/api/v2/users': page([ANNA, BRAM]) });
    storeProfile(ANNA.id);
    renderApp();

    expect(await screen.findByTestId('current-profile')).toHaveTextContent('Anna');
    const group = screen.getByRole('group', { name: 'Profiel wisselen' });
    expect(within(group).getByRole('button', { name: 'Wissel naar Anna' })).toHaveAttribute('aria-pressed', 'true');

    fireEvent.click(within(group).getByRole('button', { name: 'Wissel naar Bram de Vries' }));

    expect(screen.getByTestId('current-profile')).toHaveTextContent('Bram de Vries');
    expect(within(group).getByRole('button', { name: 'Wissel naar Bram de Vries' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(getActiveProfileId()).toBe(BRAM.id);
  });

  it('returns to the picker when the stored profile has become inactive', async () => {
    mockApi({ '/api/v2/users': page([makeUser({ ...ANNA, active: false }), BRAM]) });
    storeProfile(ANNA.id);
    renderApp();

    expect(await screen.findByRole('heading', { name: 'Wie ben jij?' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Anna/ })).not.toBeInTheDocument();
    await waitFor(() => expect(window.localStorage.getItem('huishoudplanner.profileId')).toBeNull());
  });

  it('sends the chosen profile with API requests', async () => {
    const fetchMock = mockApi({ '/api/v2/users': page([ANNA, BRAM]), 'POST /api/ping': { ok: true } });
    storeProfile(BRAM.id);
    renderApp();
    await screen.findByTestId('current-profile');

    await api.post('/api/ping');
    const call = fetchMock.mock.calls.find(([url]) => url === '/api/ping')!;
    expect((call[1] as RequestInit).headers).toMatchObject({ 'X-Profile-Id': BRAM.id, 'X-Client': 'web' });
  });

  it('reads the people from /api/v2/users and sends the chosen profile on v2 requests too', async () => {
    const fetchMock = mockApi({ '/api/v2/users': page([ANNA, BRAM]), '/api/v2/health': { status: 'ok', version: '1', database: 'ok' } });
    storeProfile(BRAM.id);
    renderApp();
    await screen.findByTestId('current-profile');

    await unwrap(apiV2.GET('/api/v2/health' as never));
    expect(fetchMock.mock.calls.some(([url]) => url === '/api/v2/users?limit=500')).toBe(true);
    const call = fetchMock.mock.calls.find(([url]) => url === '/api/v2/health')!;
    expect((call[1] as RequestInit).headers).toMatchObject({ 'x-profile-id': BRAM.id, 'x-client': 'web' });
  });

  it('keeps working when localStorage is unavailable', async () => {
    mockApi({ '/api/v2/users': page([ANNA, BRAM]) });
    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    resetProfileStore();
    renderApp();

    fireEvent.click(await screen.findByRole('button', { name: /Anna/ }));
    expect(await screen.findByTestId('current-profile')).toHaveTextContent('Anna');
    expect(getActiveProfileId()).toBe(ANNA.id);
    getItem.mockRestore();
    setItem.mockRestore();
  });

  it('shows an error when users cannot be loaded', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('{"code":"internal_error"}', { status: 500 })));
    renderApp();
    expect(await screen.findByRole('alert')).toHaveTextContent('Er ging iets mis.');
  });
});

describe('initials', () => {
  it.each([
    ['Anna', 'AN'],
    ['Bram de Vries', 'BV'],
    ['  ', '?'],
  ])('%s → %s', (name, expected) => {
    expect(initials(name)).toBe(expected);
  });
});
