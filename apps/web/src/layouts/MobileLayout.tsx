import type { ReactElement } from 'react';
import { CalendarRange, Egg, Hourglass, ListChecks, Settings, Sun, type LucideIcon } from 'lucide-react';
import { NavLink, Navigate, Route, Routes } from 'react-router';
import { Button } from '@/components/ui/button';
import { AppVersion } from '@/components/AppVersion';
import { FilterResetButton } from '@/components/FilterReset';
import { LanguageSwitcher } from '@/components/LanguageSwitcher';
import { ThemeSwitcher } from '@/components/ThemeSwitcher';
import { cn } from '@/lib/utils';
import { DuePage } from '../features/due/DuePage.tsx';
import { MobileTasksPage } from '../features/mobile-tasks/MobileTasksPage.tsx';
import { RewardPage } from '../features/reward/RewardPage.tsx';
import { TodayPage } from '../features/today/TodayPage.tsx';
import { WeekPage } from '../features/week/WeekPage.tsx';
import { t, type MessageKey } from '../i18n/nl.ts';
import { ProfileSwitcher } from '../identity/index.ts';
import { PlaceholderPage } from './PlaceholderPage.tsx';

/** Implemented pages; other tabs show a placeholder until their task is done. */
const PAGES: Partial<Record<string, ReactElement>> = {
  '/today': <TodayPage />,
  '/': <WeekPage />,
  '/due': <DuePage />,
  '/tasks': <MobileTasksPage />,
  '/reward': <RewardPage />,
};

const TABS: { path: string; label: MessageKey; icon: LucideIcon }[] = [
  { path: '/', label: 'nav.week', icon: CalendarRange },
  { path: '/today', label: 'nav.today', icon: Sun },
  { path: '/tasks', label: 'nav.tasks', icon: ListChecks },
  { path: '/due', label: 'nav.due', icon: Hourglass },
  { path: '/reward', label: 'nav.reward', icon: Egg },
];

export function MobileLayout({ onOpenManagement }: { onOpenManagement: () => void }) {
  return (
    <div className="flex min-h-screen flex-col">
      <header className="sticky top-0 z-20 flex min-h-16 flex-wrap items-center justify-between gap-x-2 gap-y-1 border-b bg-background/85 px-2 py-1 backdrop-blur sm:px-4">
        <div className="flex min-w-0 items-center gap-2">
          <ProfileSwitcher />
          {/* Phones have no room for the version next to the header controls; it stays available to screen readers. */}
          <AppVersion className="shrink-0 max-sm:sr-only" />
        </div>
        <div className="ml-auto flex items-center gap-0.5 sm:gap-1">
          <FilterResetButton className="max-sm:size-9" />
          <LanguageSwitcher />
          <ThemeSwitcher />
          <Button
            variant="ghost"
            size="icon-lg"
            className="rounded-full text-muted-foreground max-sm:size-9"
            aria-label={t('layout.openManagement')}
            title={t('layout.openManagement')}
            onClick={onOpenManagement}
          >
            <Settings aria-hidden="true" />
          </Button>
        </div>
      </header>
      <main className="mx-auto w-full max-w-6xl flex-1 px-4 pt-5 pb-28">
        <Routes>
          {TABS.map((tab) => (
            <Route
              key={tab.path}
              path={tab.path}
              element={PAGES[tab.path] ?? <PlaceholderPage titleKey={tab.label} />}
            />
          ))}
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
      <nav
        aria-label={t('nav.main')}
        className="fixed inset-x-0 bottom-0 z-20 border-t bg-card/95 px-1 pt-2 sm:px-4 pb-[max(0.5rem,env(safe-area-inset-bottom))] backdrop-blur"
      >
        <div className="mx-auto grid max-w-xl grid-cols-5 gap-0.5 sm:gap-2">
          {TABS.map(({ path, label, icon: Icon }) => (
            <NavLink
              key={path}
              to={path}
              className={({ isActive }) =>
                cn(
                  'flex min-h-14 min-w-0 flex-col items-center justify-center gap-1 rounded-2xl px-0.5 text-center text-[0.6875rem] leading-tight font-bold text-muted-foreground transition-colors [overflow-wrap:anywhere] hyphens-auto focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring sm:text-xs',
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
