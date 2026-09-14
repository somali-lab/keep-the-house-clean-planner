import type { AppConfig } from '../../config.ts';
import { HomeAssistantNotifier } from './homeassistant.ts';
import { NtfyNotifier } from './ntfy.ts';

export interface NotifyMessage {
  title: string;
  body: string;
  /** Structured fields for receivers that automate on them (Home Assistant). */
  data: Record<string, unknown>;
}

export interface Notifier {
  readonly type: 'ntfy' | 'homeassistant';
  /** Rejects with NotifyError on a non-2xx answer or network failure. */
  send(message: NotifyMessage): Promise<void>;
}

export interface NotifierOptions {
  url: string;
  token: string | undefined;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
}

export class NotifyError extends Error {
  readonly status: number | null;
  constructor(type: Notifier['type'], status: number | null, cause?: unknown) {
    // Never include the URL or token: an ntfy topic URL is effectively a secret.
    super(status === null ? `notify ${type} request failed` : `notify ${type} failed with HTTP ${status}`, { cause });
    this.name = 'NotifyError';
    this.status = status;
  }
}

export const DEFAULT_NOTIFY_TIMEOUT_MS = 10_000;

/** Resolved at call time so tests can stub the global fetch. */
export const globalFetch: typeof fetch = (...args) => fetch(...args);

/** POSTs and maps every failure to NotifyError. */
export async function postNotification(
  type: Notifier['type'],
  options: NotifierOptions,
  init: { headers: Record<string, string>; body: string },
): Promise<void> {
  const doFetch = options.fetchImpl ?? globalFetch;
  const headers = { ...init.headers, ...(options.token ? { Authorization: `Bearer ${options.token}` } : {}) };
  let res: Response;
  try {
    res = await doFetch(options.url, {
      method: 'POST',
      headers,
      body: init.body,
      signal: AbortSignal.timeout(options.timeoutMs ?? DEFAULT_NOTIFY_TIMEOUT_MS),
    });
  } catch (err) {
    throw new NotifyError(type, null, err);
  }
  if (!res.ok) throw new NotifyError(type, res.status);
}

/** Null when notifications are off (NOTIFY_TYPE=none). */
export function createNotifier(
  config: AppConfig['notify'],
  options: Pick<NotifierOptions, 'fetchImpl' | 'timeoutMs'> = {},
): Notifier | null {
  if (config.type === 'none' || !config.url) return null;
  const resolved: NotifierOptions = { url: config.url, token: config.token, ...options };
  return config.type === 'ntfy' ? new NtfyNotifier(resolved) : new HomeAssistantNotifier(resolved);
}
