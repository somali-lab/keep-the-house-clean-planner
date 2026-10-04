import type { Badge } from './api.ts';
import { Medal } from 'lucide-react';
import { cn } from '@/lib/utils';

/**
 * The picture of a badge. The alternative text is the name of the badge; a badge without an uploaded image shows a
 * standard medal, which is named the same way (ADR-0014). `muted` shows a badge that is not earned yet: the text next
 * to it says so, so the dimmed picture is never the only cue.
 */
export function BadgeImage({
  badge,
  muted = false,
  className,
}: {
  badge: Pick<Badge, 'name' | 'image'>;
  muted?: boolean;
  className?: string;
}) {
  const frame = cn('size-14 shrink-0 rounded-xl', muted && 'opacity-50 grayscale', className);
  if (badge.image) {
    return <img src={badge.image.url} alt={badge.name} width={56} height={56} loading="lazy" className={cn(frame, 'bg-secondary object-cover')} />;
  }
  return (
    <span role="img" aria-label={badge.name} className={cn(frame, 'grid place-items-center bg-accent text-accent-foreground')}>
      <Medal className="size-7" aria-hidden="true" />
    </span>
  );
}
