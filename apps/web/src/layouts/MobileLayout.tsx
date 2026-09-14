import type { ReactElement } from 'react';
import { CalendarRange, Hourglass, Monitor, Sun, type LucideIcon } from 'lucide-react';
import { NavLink, Navigate, Route, Routes } from 'react-router';
import { Button } from '@/components/ui/button';
import { LanguageSwitcher } from '@/components/LanguageSwitcher';
import { ThemeSwitcher } from '@/components/ThemeSwitcher';
import { cn } from '@/lib/utils';
import { DuePage } from '../features/due/DuePage.tsx';
import { TodayPage } from '../features/today/TodayPage.tsx';
import { WeekPage } from '../features/week/WeekPage.tsx';
import { t, type MessageKey } from '../i18n/nl.ts';
import { ProfileSwitcher } from '../identity/index.ts';
import { PlaceholderPage } from './PlaceholderPage.tsx';

/** Implemented pages; other tabs show a placeholder until their task is done. */
const PAGES: Partial<Record<string, ReactElement>> = {
  '/vandaag': <TodayPage />,
  '/week': <WeekPage />,
  '/achterstand': <DuePage />,
};

const TABS: { path: string; label: MessageKey; icon: LucideIcon }[] = [
  { path: '/week', label: 'nav.week', icon: CalendarRange },
  { path: '/vandaag', label: 'nav.today', icon: Sun },
  { path: '/achterstand', label: 'nav.due', icon: Hourglass },
];

export function MobileLayout({ onSwitchLayout }: { onSwitchLayout: () => void }) {
  return (
    <div className="flex min-h-screen flex-col">
      <header className="sticky top-0 z-20 flex h-16 items-center justify-between gap-2 border-b bg-background/85 px-4 backdrop-blur">
        <ProfileSwitcher />
        <div className="flex items-center gap-1">
          <LanguageSwitcher />
          <ThemeSwitcher />
          <Button
            variant="ghost"
            size="icon-lg"
            className="rounded-full text-muted-foreground"
            aria-label={t('layout.switchToDesktop')}
            title={t('layout.switchToDesktop')}
            onClick={onSwitchLayout}
          >
            <Monitor aria-hidden="true" />
          </Button>
        </div>
      </header>
      <main className="mx-auto w-full max-w-5xl flex-1 px-4 pt-5 pb-28">
        <Routes>
          {TABS.map((tab) => (
            <Route
              key={tab.path}
              path={tab.path}
              element={PAGES[tab.path] ?? <PlaceholderPage titleKey={tab.label} />}
            />
          ))}
          <Route path="*" element={<Navigate to="/week" replace />} />
        </Routes>
      </main>
      <nav
        aria-label={t('nav.main')}
        className="fixed inset-x-0 bottom-0 z-20 border-t bg-card/95 px-4 pt-2 pb-[max(0.5rem,env(safe-area-inset-bottom))] backdrop-blur"
      >
        <div className="mx-auto grid max-w-xl grid-cols-3 gap-2">
          {TABS.map(({ path, label, icon: Icon }) => (
            <NavLink
              key={path}
              to={path}
              className={({ isActive }) =>
                cn(
                  'flex min-h-14 flex-col items-center justify-center gap-1 rounded-2xl text-xs font-bold text-muted-foreground transition-colors',
                  isActive && 'bg-primary/10 text-primary',
                )
              }
            >
              <Icon className="size-6" aria-hidden="true" />
              {t(label)}
            </NavLink>
          ))}
        </div>
      </nav>
    </div>
  );
}
