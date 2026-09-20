import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PwaStatus } from './PwaStatus.tsx';

const pwa = vi.hoisted(() => ({
  needRefresh: false,
  updateServiceWorker: vi.fn(() => Promise.resolve()),
  onRegisteredSW: undefined as
    | ((url: string, registration: ServiceWorkerRegistration | undefined) => void)
    | undefined,
}));

vi.mock('virtual:pwa-register/react', () => ({
  useRegisterSW: (options: {
    onRegisteredSW?: (url: string, registration: ServiceWorkerRegistration | undefined) => void;
  }) => {
    pwa.onRegisteredSW = options.onRegisteredSW;
    return {
      needRefresh: [pwa.needRefresh, vi.fn()],
      offlineReady: [false, vi.fn()],
      updateServiceWorker: pwa.updateServiceWorker,
    };
  },
}));

function setOnline(online: boolean) {
  Object.defineProperty(window.navigator, 'onLine', { configurable: true, value: online });
}

describe('PWA status', () => {
  beforeEach(() => {
    setOnline(true);
    pwa.needRefresh = false;
    pwa.updateServiceWorker.mockReset();
    pwa.updateServiceWorker.mockResolvedValue(undefined);
    pwa.onRegisteredSW = undefined;
  });

  it('explains that saved data is shown while the app is offline', () => {
    setOnline(false);
    render(<PwaStatus />);

    expect(screen.getByRole('status')).toHaveTextContent('Je bent offline');
    expect(screen.getByRole('status')).toHaveTextContent('laatst opgeslagen gegevens');

    setOnline(true);
    fireEvent(window, new Event('online'));
    expect(screen.queryByText('Je bent offline')).not.toBeInTheDocument();
  });

  it('offers one explicit action when a new app version is ready', async () => {
    pwa.needRefresh = true;
    render(<PwaStatus />);

    fireEvent.click(screen.getByRole('button', { name: 'Nu bijwerken' }));

    expect(pwa.updateServiceWorker).toHaveBeenCalledWith(true);
    expect(screen.getByRole('button', { name: 'Bijwerken…' })).toBeDisabled();
  });

  it('checks for a newer service worker when the app receives focus', async () => {
    const update = vi.fn(() => Promise.resolve());
    render(<PwaStatus />);
    pwa.onRegisteredSW?.('/sw.js', { update } as unknown as ServiceWorkerRegistration);

    fireEvent.focus(window);

    await waitFor(() => expect(update).toHaveBeenCalledOnce());
  });
});
