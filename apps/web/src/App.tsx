import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { BrowserRouter } from 'react-router';
import { t } from './i18n/nl.ts';
import { LanguageProvider } from './i18n/LanguageProvider.tsx';
import { ProfilePicker, ProfileProvider, useProfile } from './identity/index.ts';
import { DesktopLayout } from './layouts/DesktopLayout.tsx';
import { MobileLayout } from './layouts/MobileLayout.tsx';
import { useIsDesktop } from './layouts/useIsDesktop.ts';
import { OfflineSyncProvider } from './offline/OfflineSyncProvider.tsx';
import { ThemeProvider } from './theme/ThemeProvider.tsx';

type LayoutChoice = 'auto' | 'desktop' | 'mobile';

export function AppShell() {
  const { status, profile } = useProfile();
  const isDesktop = useIsDesktop();
  const [choice, setChoice] = useState<LayoutChoice>('auto');

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

  const desktop = choice === 'auto' ? isDesktop : choice === 'desktop';
  return desktop ? (
    <DesktopLayout onSwitchLayout={() => setChoice('mobile')} />
  ) : (
    <MobileLayout onSwitchLayout={() => setChoice('desktop')} />
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
