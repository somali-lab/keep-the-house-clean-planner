import { cn } from '@/lib/utils';
import { APP_VERSION } from '../version.ts';
import { format } from '../i18n/nl.ts';

export function AppVersion({ className }: { className?: string }) {
  return (
    <span
      className={cn('text-[0.65rem] leading-none font-semibold text-muted-foreground', className)}
      aria-label={format('app.version', { version: APP_VERSION })}
    >
      v{APP_VERSION}
    </span>
  );
}
