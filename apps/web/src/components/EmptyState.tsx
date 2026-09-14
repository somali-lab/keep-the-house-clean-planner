import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

/** Friendly, centered placeholder for lists without items. */
export function EmptyState({
  icon,
  children,
  className,
}: {
  icon?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <div
      className={cn(
        'flex flex-col items-center justify-center gap-3 rounded-2xl border border-dashed bg-card/60 px-6 py-10 text-center text-muted-foreground',
        className,
      )}
    >
      {icon && <div className="grid size-12 place-items-center rounded-full bg-secondary text-secondary-foreground">{icon}</div>}
      <div className="max-w-sm">{children}</div>
    </div>
  );
}
