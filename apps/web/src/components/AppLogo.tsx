import { House } from 'lucide-react';
import { cn } from '@/lib/utils';
import { t } from '../i18n/nl.ts';

export function AppLogo({ className, compact = false }: { className?: string; compact?: boolean }) {
  return (
    <span className={cn('flex items-center gap-2.5 font-extrabold tracking-tight', className)}>
      <span className="grid size-9 place-items-center rounded-xl bg-primary text-primary-foreground shadow-sm">
        <House className="size-5" aria-hidden="true" />
      </span>
      <span className={cn(compact && 'visually-hidden')}>{t('app.name')}</span>
    </span>
  );
}
