import type { UserRole } from '@huishoudplanner/shared';
import type { ReactElement } from 'react';
import { useState } from 'react';
import {
  ChartColumnBig,
  Scale,
  History,
  ListChecks,
  type LucideIcon,
  CalendarDays,
  House,
  Settings,
  ChevronLeft,
  ChevronRight,
} from 'lucide-react';
import { NavLink, Navigate, Route, Routes } from 'react-router';
import { AppLogo } from '@/components/AppLogo';
import { AppVersion } from '@/components/AppVersion';
import { LanguageSwitcher } from '@/components/LanguageSwitcher';
import { ThemeSwitcher } from '@/components/ThemeSwitcher';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { DistributionPage } from '../features/distribution/DistributionPage.tsx';
import { HistoryPage } from '../features/history/HistoryPage.tsx';
import { PlannerPage } from '../features/planner/PlannerPage.tsx';
import { SettingsPage } from '../features/settings/SettingsPage.tsx';
import { StatsPage } from '../features/stats/StatsPage.tsx';
import { TasksPage } from '../features/tasks/TasksPage.tsx';
import { t, type MessageKey } from '../i18n/nl.ts';
import { ProfileSwitcher, useProfile } from '../identity/index.ts';
import { PlaceholderPage } from './PlaceholderPage.tsx';

/** Implemented pages; other sections show a placeholder until their task is done. */
const PAGES: Partial<Record<string, ReactElement>> = {
  '/manage/planner': <PlannerPage />,
  '/manage/tasks': <TasksPage />,
  '/manage/distribution': <DistributionPage />,
  '/manage/statistics': <StatsPage />,
  '/manage/history': <HistoryPage />,
  '/manage/settings': <SettingsPage />,
};

const SECTIONS: { path: string; label: MessageKey; icon: LucideIcon; minimumRole: UserRole }[] = [
  { path: '/manage/planner', label: 'nav.planner', icon: CalendarDays, minimumRole: 'planner' },
  { path: '/manage/tasks', label: 'nav.tasks', icon: ListChecks, minimumRole: 'planner' },
  { path: '/manage/distribution', label: 'nav.distribution', icon: Scale, minimumRole: 'member' },
  { path: '/manage/statistics', label: 'nav.stats', icon: ChartColumnBig, minimumRole: 'member' },
  { path: '/manage/history', label: 'nav.history', icon: History, minimumRole: 'member' },
  { path: '/manage/settings', label: 'nav.settings', icon: Settings, minimumRole: 'admin' },
];

const ROLE_LEVEL: Record<UserRole, number> = { member: 0, planner: 1, admin: 2 };

export function DesktopLayout({ onOpenOverview }: { onOpenOverview: () => void }) {
  const [collapsed, setCollapsed] = useState(false);
  const { profile } = useProfile();
  const sections = SECTIONS.filter((section) => profile && ROLE_LEVEL[profile.role] >= ROLE_LEVEL[section.minimumRole]);

  return (
    <div
      className={cn(
        'grid min-h-screen transition-[grid-template-columns] duration-200',
        collapsed ? 'grid-cols-[4rem_1fr]' : 'grid-cols-[10.5rem_1fr]',
      )}
    >
      <aside
        className={cn(
          'relative sticky top-0 z-30 flex h-screen flex-col gap-6 border-r border-sidebar-border bg-sidebar py-6 text-sidebar-foreground transition-[padding] duration-200',
          collapsed ? 'px-2.5' : 'px-3',
        )}
      >
        <div className="flex h-9 items-center justify-center">
          <AppLogo compact className="justify-center" />
        </div>
        <Button
          variant="outline"
          size="icon-sm"
          className="absolute top-1/2 right-0 z-40 -translate-y-1/2 translate-x-1/2 rounded-full bg-card text-muted-foreground shadow-md hover:text-foreground"
          aria-label={t(collapsed ? 'layout.expandMenu' : 'layout.collapseMenu')}
          aria-expanded={!collapsed}
          onClick={() => setCollapsed((value) => !value)}
        >
          {collapsed ? <ChevronRight aria-hidden="true" /> : <ChevronLeft aria-hidden="true" />}
        </Button>
        <nav aria-label={t('nav.main')} className="flex-1">
          <ul className="flex flex-col gap-1">
            {sections.map(({ path, label, icon: Icon }) => (
              <li key={path}>
                <NavLink
                  to={path}
                  title={collapsed ? t(label) : undefined}
                  className={({ isActive }) =>
                    cn(
                      'flex h-11 items-center gap-3 rounded-xl px-3 text-sm font-semibold text-sidebar-foreground/80 transition-colors hover:bg-sidebar-accent hover:text-sidebar-foreground',
                      collapsed && 'justify-center px-0',
                      isActive && 'bg-card text-primary shadow-sm hover:bg-card hover:text-primary',
                    )
                  }
                >
                  <Icon className="size-5" aria-hidden="true" />
                  <span className={cn(collapsed && 'visually-hidden')}>{t(label)}</span>
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
        <div className="mt-auto grid gap-2">
          <ProfileSwitcher sidebar compact={collapsed} />
          <AppVersion className="block text-center" />
        </div>
      </aside>
      <div className="flex min-w-0 flex-col">
        <header className="sticky top-0 z-20 flex h-16 items-center justify-end gap-2 border-b bg-background/90 px-6 backdrop-blur">
          <LanguageSwitcher />
          <ThemeSwitcher />
          <Button
            variant="ghost"
            size="icon-lg"
            className="rounded-full text-muted-foreground"
            aria-label={t('layout.openOverview')}
            title={t('layout.openOverview')}
            onClick={onOpenOverview}
          >
            <House aria-hidden="true" />
          </Button>
        </header>
        <main className="mx-auto w-full max-w-[96rem] flex-1 px-6 py-7">
          <Routes>
            {sections.map((section) => (
              <Route
                key={section.path}
                path={section.path}
                element={PAGES[section.path] ?? <PlaceholderPage titleKey={section.label} />}
              />
            ))}
            <Route path="*" element={<Navigate to={sections[0]?.path ?? '/manage/distribution'} replace />} />
          </Routes>
        </main>
      </div>
    </div>
  );
}
