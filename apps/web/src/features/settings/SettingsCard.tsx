import type { ReactNode } from 'react';
import { CircleCheck, TriangleAlert } from 'lucide-react';
import { cn } from '@/lib/utils';

/** Card surface shared by every settings section (used on <form> and <section>). */
export const settingsCardClass = 'flex flex-col gap-5 rounded-2xl border bg-card p-6 text-card-foreground shadow-sm';

/** Icon + h2 title + short muted description at the top of a settings card. */
export function SettingsCardHeader({
  icon,
  titleId,
  title,
  description,
}: {
  icon: ReactNode;
  titleId: string;
  title: ReactNode;
  description?: ReactNode;
}) {
  return (
    <div className="flex items-start gap-3">
      <div className="grid size-10 shrink-0 place-items-center rounded-xl bg-accent text-accent-foreground [&_svg]:size-5">
        {icon}
      </div>
      <div className="min-w-0">
        <h2 id={titleId} className="text-lg leading-tight font-bold">
          {title}
        </h2>
        {description && <p className="mt-1 text-sm text-muted-foreground">{description}</p>}
      </div>
    </div>
  );
}

/** Label above its control. */
export function Field({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('flex flex-col gap-2', className)}>{children}</div>;
}

/** Right-aligned row of form buttons. */
export function FormActions({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('flex flex-wrap items-center justify-end gap-2', className)}>{children}</div>;
}

/** Status (success) or alert (error) message; keeps the live-region role. */
export function FormMessage({ kind, children }: { kind: 'status' | 'alert'; children: ReactNode }) {
  const Icon = kind === 'status' ? CircleCheck : TriangleAlert;
  return (
    <p
      role={kind}
      className={cn(
        'flex items-center gap-2 rounded-xl px-3 py-2 text-sm font-semibold',
        kind === 'status' ? 'bg-success/15 text-success' : 'bg-destructive/10 text-destructive',
      )}
    >
      <Icon className="size-4 shrink-0" aria-hidden="true" />
      <span className="min-w-0 [overflow-wrap:anywhere]">{children}</span>
    </p>
  );
}

/** Native checkbox with its label, styled as a quiet inline check. */
export const checkboxClass = 'size-4 shrink-0 rounded accent-primary';

/** Row in a settings list. */
export const listRowClass =
  'flex flex-wrap items-center gap-3 rounded-xl border bg-background/60 px-4 py-3 transition-colors hover:bg-secondary/50';
