import type { CSSProperties } from 'react';
import { cn } from '@/lib/utils';

export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0]!.slice(0, 2).toUpperCase();
  return (parts[0]![0]! + parts.at(-1)![0]!).toUpperCase();
}

const SIZES = {
  sm: 'size-7 text-[0.7rem]',
  md: 'size-9 text-xs',
  lg: 'size-16 text-xl',
} as const;

export function Avatar({
  name,
  color,
  size = 'md',
  className,
}: {
  name: string;
  color: string;
  size?: keyof typeof SIZES;
  className?: string;
}) {
  return (
    <span
      className={cn(
        'user-color inline-grid shrink-0 place-items-center rounded-full font-extrabold text-white shadow-sm ring-2 ring-card',
        SIZES[size],
        className,
      )}
      style={{ '--user-color': color } as CSSProperties}
      aria-hidden="true"
    >
      {initials(name)}
    </span>
  );
}
