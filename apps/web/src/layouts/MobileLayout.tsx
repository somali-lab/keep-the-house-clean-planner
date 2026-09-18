import type { ReactElement } from 'react';
import { CalendarRange, Hourglass, ListChecks, Settings, Sun, type LucideIcon } from 'lucide-react';
import { NavLink, Navigate, Route, Routes } from 'react-router';
import { Button } from '@/components/ui/button';
import { AppVersion } from '@/components/AppVersion';
import { LanguageSwitcher } from '@/components/LanguageSwitcher';
import { ThemeSwitcher } from '@/components/ThemeSwitcher';
import { cn } from '@/lib/utils';
import { DuePage } from '../features/due/DuePage.tsx';
import { MobileTasksPage } from '../features/mobile-tasks/MobileTasksPage.tsx';
import { TodayPage } from '../features/today/TodayPage.tsx';
import { WeekPage } from '../features/week/WeekPage.tsx';
import { t, type MessageKey } from '../i18n/nl.ts';
import { ProfileSwitcher } from '../identity/index.ts';
import { PlaceholderPage } from './PlaceholderPage.tsx';

/** Implemented pages; other tabs show a placeholder until their task is done. */
const PAGES: Partial<Record<string, ReactElement>> = {
  '/mobile/today': <TodayPage />,
  '/mobile/week': <WeekPage />,
  '/mobile/due': <DuePage />,
  '/mobile/tasks': <MobileTasksPage />,
};

const TABS: { path: string; label: MessageKey; icon: LucideIcon }[] = [
  { path: '/mobile/week', label: 'nav.week', icon: CalendarRange },
  { path: '/mobile/today', label: 'nav.today', icon: Sun },
  { path: '/mobile/tasks', label: 'nav.tasks', icon: ListChecks },
  { path: '/mobile/due', label: 'nav.due', icon: Hourglass },
];

export function MobileLayout({ onOpenManagement }: { onOpenManagement: () => void }) {
  return (
    <div className="flex min-h-screen flex-col">
      <header className="sticky top-0 z-20 flex h-16 items-center justify-between gap-2 border-b bg-background/85 px-4 backdrop-blur">
        <div className="flex min-w-0 items-center gap-2">
          <ProfileSwitcher />
          <AppVersion className="shrink-0" />
        </div>
        <div className="flex items-center gap-1">
          <LanguageSwitcher />
          <ThemeSwitcher />
          <Button
            variant="ghost"
            size="icon-lg"
            className="rounded-full text-muted-foreground"
            aria-label={t('layout.openManagement')}
            title={t('layout.openManagement')}
            onClick={onOpenManagement}
          >
            <Settings aria-hidden="true" />
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
          <Route path="*" element={<Navigate to="/mobile/week" replace />} />
        </Routes>
      </main>
      <nav
        aria-label={t('nav.main')}
        className="fixed inset-x-0 bottom-0 z-20 border-t bg-card/95 px-4 pt-2 pb-[max(0.5rem,env(safe-area-inset-bottom))] backdrop-blur"
      >
        <div className="mx-auto grid max-w-xl grid-cols-4 gap-1 sm:gap-2">
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
