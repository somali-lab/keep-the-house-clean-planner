/** Thin adapter over the browser Notification API; read at call time so tests can stub it. */

export type PermissionState = 'insecure' | 'unsupported' | 'default' | 'granted' | 'denied';

function api(): typeof Notification | null {
  return typeof window !== 'undefined' && 'Notification' in window ? window.Notification : null;
}

/**
 * Permission is per browser and device, not per person. Browsers only offer
 * notifications on HTTPS or localhost, so a plain-HTTP address is reported
 * as 'insecure' before anything else.
 */
export function getPermissionState(): PermissionState {
  if (typeof window !== 'undefined' && window.isSecureContext === false) return 'insecure';
  const notification = api();
  return notification ? notification.permission : 'unsupported';
}

/** Must be called from a click: browsers ignore prompts that the person did not ask for. */
export async function requestPermission(): Promise<PermissionState> {
  const notification = api();
  if (getPermissionState() === 'insecure') return 'insecure';
  if (!notification) return 'unsupported';
  try {
    await notification.requestPermission();
  } catch {
    // Older browsers only take a callback; the state below still reflects the answer.
  }
  return getPermissionState();
}

export interface ShownNotification {
  title: string;
  body: string;
  /** Notifications with the same tag replace each other, which backs up the one-summary rule. */
  tag?: string;
}

/** Shows the notification; false when permission is missing or the browser refused. */
export async function showNotification({ title, body, tag }: ShownNotification): Promise<boolean> {
  const notification = api();
  if (!notification || getPermissionState() !== 'granted') return false;
  const options: NotificationOptions = { body, icon: '/icon.svg', ...(tag ? { tag } : {}) };
  try {
    new notification(title, options);
    return true;
  } catch {
    // Some browsers only allow notifications through a service worker registration.
    try {
      const registration = await navigator.serviceWorker?.getRegistration();
      if (!registration) return false;
      await registration.showNotification(title, options);
      return true;
    } catch {
      return false;
    }
  }
}
