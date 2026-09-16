import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { BrowserRouter, Navigate, useLocation, useNavigate } from 'react-router';
import { t } from './i18n/nl.ts';
import { LanguageProvider } from './i18n/LanguageProvider.tsx';
import { ProfilePicker, ProfileProvider, useProfile } from './identity/index.ts';
import { DesktopLayout } from './layouts/DesktopLayout.tsx';
import { MobileLayout } from './layouts/MobileLayout.tsx';
import { useIsDesktop } from './layouts/useIsDesktop.ts';
import { OfflineSyncProvider } from './offline/OfflineSyncProvider.tsx';
import { ThemeProvider } from './theme/ThemeProvider.tsx';

type LayoutChoice = 'auto' | 'desktop' | 'mobile';

const LEGACY_ROUTES: Record<string, string> = {
  '/vandaag': '/mobile/today',
  '/achterstand': '/mobile/due',
  '/taken': '/tasks',
  '/verdeling': '/distribution',
  '/statistiek': '/statistics',
  '/geschiedenis': '/history',
  '/instellingen': '/settings',
  '/weekoverzicht': '/week',
};

export function AppShell() {
  const { status, profile } = useProfile();
  const isDesktop = useIsDesktop();
  const location = useLocation();
  const navigate = useNavigate();
  const [choice, setChoice] = useState<LayoutChoice>('auto');

  const legacyTarget = LEGACY_ROUTES[location.pathname];
  if (legacyTarget) {
    return (
      <Navigate
        to={{ pathname: legacyTarget, search: location.search, hash: location.hash }}
        replace
      />
    );
  }
  if (location.pathname === '/mobile') return <Navigate to="/mobile/week" replace />;

  if (status === 'loading')
    return (
      <p role="status" className="grid min-h-screen place-items-center text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  if (status === 'error')
    return (
      <p
        role="alert"
        className="grid min-h-screen place-items-center font-semibold text-destructive"
      >
        {t('app.error')}
      </p>
    );
  if (!profile) return <ProfilePicker />;

  const mobileUrl = location.pathname.startsWith('/mobile/');
  const desktop = mobileUrl ? false : choice === 'auto' ? isDesktop : choice === 'desktop';
  return desktop ? (
    <DesktopLayout
      onSwitchLayout={() => {
        setChoice('mobile');
        navigate('/mobile/week');
      }}
    />
  ) : (
    <MobileLayout
      onSwitchLayout={() => {
        setChoice('desktop');
        navigate('/week');
      }}
    />
  );
}

export function App({ queryClient }: { queryClient?: QueryClient }) {
  const [client] = useState(() => queryClient ?? new QueryClient());
  return (
    <ThemeProvider>
      <QueryClientProvider client={client}>
        <ProfileProvider>
          <OfflineSyncProvider>
            <BrowserRouter>
              <LanguageProvider>
                <AppShell />
              </LanguageProvider>
            </BrowserRouter>
          </OfflineSyncProvider>
        </ProfileProvider>
      </QueryClientProvider>
    </ThemeProvider>
  );
}
