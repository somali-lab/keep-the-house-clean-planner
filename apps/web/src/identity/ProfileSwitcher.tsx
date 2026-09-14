import { cn } from '@/lib/utils';
import { format, t } from '../i18n/nl.ts';
import { Avatar } from './Avatar.tsx';
import { useProfile } from './ProfileProvider.tsx';

/** Header control: current profile name plus one tap per profile to switch. */
export function ProfileSwitcher({ sidebar = false, compact = false }: { sidebar?: boolean; compact?: boolean }) {
  const { activeUsers, profile, selectProfile } = useProfile();
  return (
    <div
      className={cn(
        'flex items-center gap-3',
        sidebar && 'w-full flex-col gap-2 rounded-xl border bg-card/60 p-2 shadow-sm',
        sidebar && compact && 'p-1.5',
      )}
    >
      <div
        role="group"
        aria-label={t('profile.switcher')}
        className={cn(
          'flex items-center -space-x-1',
          sidebar && compact && 'flex-col space-x-0 -space-y-1',
        )}
      >
        {activeUsers.map((user) => {
          const isCurrent = user._id === profile?._id;
          return (
            <button
              key={user._id}
              type="button"
              aria-pressed={isCurrent}
              aria-label={format('profile.switchTo', { name: user.name })}
              title={user.name}
              onClick={() => selectProfile(user._id)}
              className={cn(
                'grid size-11 place-items-center rounded-full outline-none transition-all focus-visible:ring-[3px] focus-visible:ring-ring/50',
                sidebar && 'size-9',
                isCurrent ? 'z-10 scale-105' : 'opacity-55 grayscale-[35%] hover:opacity-100 hover:grayscale-0',
              )}
            >
              <Avatar
                name={user.name}
                color={user.color}
                className={cn(isCurrent && 'ring-primary ring-offset-2 ring-offset-background')}
              />
            </button>
          );
        })}
      </div>
      <span
        className={cn('min-w-0 max-w-full text-sm leading-tight', sidebar && 'text-center', compact && 'visually-hidden')}
        aria-live="polite"
      >
        <span className="visually-hidden">{t('profile.current')}: </span>
        <strong data-testid="current-profile" className="block truncate font-bold">
          {profile?.name}
        </strong>
      </span>
    </div>
  );
}
