import { RefreshCw, WifiOff } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useRegisterSW } from 'virtual:pwa-register/react';
import { Button } from '@/components/ui/button';
import { t } from '../i18n/nl.ts';

export const UPDATE_CHECK_INTERVAL_MS = 5 * 60 * 1000;

/** Makes stale/offline state explicit and gives new builds one reliable update action. */
export function PwaStatus() {
  const [online, setOnline] = useState(() => navigator.onLine);
  const [updating, setUpdating] = useState(false);
  const registration = useRef<ServiceWorkerRegistration | undefined>(undefined);
  const {
    needRefresh: [needRefresh],
    updateServiceWorker,
  } = useRegisterSW({
    immediate: true,
    onRegisteredSW: (_url, nextRegistration) => {
      registration.current = nextRegistration;
    },
  });

  useEffect(() => {
    const checkForUpdate = () => {
      if (navigator.onLine) void registration.current?.update();
    };
    const onOnline = () => {
      setOnline(true);
      checkForUpdate();
    };
    const onOffline = () => setOnline(false);
    const onVisibilityChange = () => {
      if (document.visibilityState === 'visible') checkForUpdate();
    };
    window.addEventListener('online', onOnline);
    window.addEventListener('offline', onOffline);
    window.addEventListener('focus', checkForUpdate);
    document.addEventListener('visibilitychange', onVisibilityChange);
    const timer = window.setInterval(checkForUpdate, UPDATE_CHECK_INTERVAL_MS);
    return () => {
      window.removeEventListener('online', onOnline);
      window.removeEventListener('offline', onOffline);
      window.removeEventListener('focus', checkForUpdate);
      document.removeEventListener('visibilitychange', onVisibilityChange);
      window.clearInterval(timer);
    };
  }, []);

  if (online && !needRefresh) return null;

  return (
    <div className="fixed inset-x-3 top-3 z-[80] mx-auto grid max-w-xl gap-2" aria-live="polite">
      {!online && (
        <div className="flex items-start gap-3 rounded-2xl border border-warning bg-card p-4 shadow-xl" role="status">
          <WifiOff className="mt-0.5 size-5 shrink-0 text-warning-foreground" aria-hidden="true" />
          <div>
            <p className="font-extrabold">{t('offline.title')}</p>
            <p className="text-sm text-muted-foreground">{t('offline.status')}</p>
          </div>
        </div>
      )}
      {needRefresh && (
        <div className="flex flex-wrap items-center gap-3 rounded-2xl border bg-card p-4 shadow-xl" role="status">
          <RefreshCw className="size-5 shrink-0 text-primary" aria-hidden="true" />
          <p className="min-w-0 flex-1 font-semibold">{t('pwa.updateAvailable')}</p>
          <Button
            type="button"
            disabled={updating}
            onClick={() => {
              setUpdating(true);
              void updateServiceWorker(true).catch(() => setUpdating(false));
            }}
          >
            <RefreshCw aria-hidden="true" />
            {t(updating ? 'pwa.updating' : 'pwa.updateNow')}
          </Button>
        </div>
      )}
    </div>
  );
}
