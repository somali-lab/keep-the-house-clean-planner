import { AppLogo } from '@/components/AppLogo';
import { t } from '../i18n/nl.ts';
import { Avatar } from './Avatar.tsx';
import { useProfile } from './ProfileProvider.tsx';

/** Fullscreen first-visit choice of profile. */
export function ProfilePicker() {
  const { activeUsers, selectProfile } = useProfile();
  return (
    <main className="flex min-h-screen flex-col items-center justify-center gap-8 px-6 py-12">
      <AppLogo className="text-lg" />
      <div className="w-full max-w-md rounded-3xl border bg-card p-8 text-center shadow-sm">
        <h1 className="text-3xl font-extrabold tracking-tight">{t('profile.choose.title')}</h1>
        <p className="mt-2 text-muted-foreground">{t('profile.choose.hint')}</p>
        {activeUsers.length === 0 ? (
          <p role="alert" className="mt-6 rounded-xl bg-destructive/10 p-4 text-destructive">
            {t('profile.none')}
          </p>
        ) : (
          <ul className="mt-8 grid grid-cols-2 gap-4">
            {activeUsers.map((user) => (
              <li key={user._id}>
                <button
                  type="button"
                  onClick={() => selectProfile(user._id)}
                  className="flex w-full flex-col items-center gap-3 rounded-2xl border-2 border-transparent bg-secondary/60 px-3 py-5 font-bold transition-all outline-none hover:-translate-y-0.5 hover:border-primary/40 hover:bg-secondary hover:shadow-md focus-visible:ring-[3px] focus-visible:ring-ring/50"
                >
                  <Avatar name={user.name} color={user.color} size="lg" />
                  <span>{user.name}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
      <p className="max-w-md text-center text-sm text-muted-foreground">{t('profile.choose.note')}</p>
    </main>
  );
}
