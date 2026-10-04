import { toDayKey } from '@huishoudplanner/shared/time';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useSettings } from '../../api/v2/household.ts';
import { useProfile } from '../../identity/index.ts';
import { fetchOpenOccurrences } from './api.ts';
import { getPermissionState, showNotification, type PermissionState } from './browserNotification.ts';
import { claimKey, claimMoment, pruneClaims } from './notificationClaim.ts';
import { buildNotificationContent, nextCheckDelay, planNotifications, type NotificationMoment } from './notificationModel.ts';

export interface DeliveryDeps {
  queryClient: QueryClient;
  inFlight: Set<string>;
}

/**
 * Delivers one due moment at most once across all tabs: claim it, look up the
 * person's open tasks, and show a single summary. Nothing open means no
 * notification. A failed lookup gives the claim back so the next check inside
 * the grace period can retry. A tab whose lookup hangs loses its pending claim
 * to another tab after CLAIM_STALE_MS and then stays silent.
 */
export async function deliverMoment(
  personId: string,
  timezone: string,
  moment: NotificationMoment,
  { queryClient, inFlight }: DeliveryDeps,
): Promise<void> {
  const key = claimKey(personId, moment);
  if (inFlight.has(key)) return;
  inFlight.add(key);
  try {
    // Without permission nothing is claimed, so granting it within the grace period still delivers.
    if (getPermissionState() !== 'granted') return;
    const claim = await claimMoment(key);
    if (!claim) return;
    try {
      const todayKey = toDayKey(new Date(), timezone);
      const occurrences = await fetchOpenOccurrences(queryClient, personId, todayKey);
      const content = buildNotificationContent(occurrences, personId, todayKey);
      if (!claim.holds()) return;
      if (!content) return claim.complete();
      if (await showNotification({ title: content.title, body: content.body, tag: key })) claim.complete();
      else claim.release();
    } catch {
      claim.release();
    }
  } finally {
    inFlight.delete(key);
  }
}

/**
 * Keeps one timer armed for the next configured moment of the person. It
 * re-evaluates when the tab becomes visible or is restored, and whenever the
 * person, their moments or the household timezone change. A moment is only
 * delivered while it is current: at the time, or within the grace period.
 */
export function useBrowserNotifications(personId: string, times: readonly string[], timezone: string | undefined): void {
  const queryClient = useQueryClient();
  const inFlight = useRef(new Set<string>());
  const timesKey = times.join(',');

  useEffect(() => {
    if (!timezone || timesKey === '') return;
    const configured = timesKey.split(',');
    let timer: ReturnType<typeof setTimeout> | undefined;

    const arm = () => {
      clearTimeout(timer);
      const now = new Date();
      const plan = planNotifications(now, timezone, configured);
      for (const moment of plan.deliver) {
        // A failed delivery must never become an unhandled rejection; the next check retries.
        deliverMoment(personId, timezone, moment, { queryClient, inFlight: inFlight.current }).catch(() => undefined);
      }
      const delay = nextCheckDelay(now, plan);
      if (delay !== null) timer = setTimeout(arm, delay);
    };
    const onVisible = () => {
      if (document.visibilityState === 'visible') arm();
    };

    pruneClaims(toDayKey(new Date(), timezone));
    document.addEventListener('visibilitychange', onVisible);
    window.addEventListener('pageshow', arm);
    arm();
    return () => {
      clearTimeout(timer);
      document.removeEventListener('visibilitychange', onVisible);
      window.removeEventListener('pageshow', arm);
    };
  }, [personId, timesKey, timezone, queryClient]);
}

function Scheduler({ personId, times }: { personId: string; times: readonly string[] }) {
  const settings = useSettings();
  useBrowserNotifications(personId, times, settings.data?.timezone);
  return null;
}

/** Permission as the page sees it, re-read whenever the tab comes back, so a later grant is noticed. */
function usePermissionState(): PermissionState {
  const [state, setState] = useState<PermissionState>(() => getPermissionState());
  useEffect(() => {
    const refresh = () => setState(getPermissionState());
    document.addEventListener('visibilitychange', refresh);
    window.addEventListener('pageshow', refresh);
    window.addEventListener('focus', refresh);
    return () => {
      document.removeEventListener('visibilitychange', refresh);
      window.removeEventListener('pageshow', refresh);
      window.removeEventListener('focus', refresh);
    };
  }, []);
  return state;
}

/**
 * Mounted once for the whole app. Does nothing, and fetches nothing, unless the
 * active profile has browser notifications enabled with at least one moment and
 * this browser can show them (permission not denied, secure origin, supported).
 */
export function BrowserNotificationHost() {
  const { profile } = useProfile();
  const permission = usePermissionState();
  const { enabled = false, times = [] } = profile?.browserNotifications ?? {};
  if (!profile || !enabled || times.length === 0) return null;
  if (permission !== 'granted' && permission !== 'default') return null;
  return <Scheduler key={profile.id} personId={profile.id} times={times} />;
}
