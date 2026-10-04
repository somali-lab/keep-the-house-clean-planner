/**
 * The service worker's cache rule for API reads (requirements 7.3). Workbox serialises these functions into the
 * generated service worker with `toString()`, so each one must stay self-contained: no imports and no reference to
 * anything outside its own body.
 */

/**
 * Which requests are kept for offline reading: every `GET` under `/api/v2/` except what must always be live or is
 * never kept (the exports and downloads, the audit log, the health check that tells whether the server is
 * reachable, and the AI endpoints).
 */
export function isCacheableApiRequest({ url, request }: { url: URL; request: { method: string } }): boolean {
  if (request.method !== 'GET' || !url.pathname.startsWith('/api/v2/')) return false;
  const area = url.pathname.slice('/api/v2/'.length).split('/')[0];
  return !['export', 'import', 'audit', 'health', 'ai', 'jobs'].includes(area ?? '');
}

/**
 * The cache key of an answer: the URL plus the profile that asked. The actor is chosen by the `X-Profile-Id`
 * header, which is not part of the URL, so a key of the URL alone would hand one person the cached answer
 * that was made for another person on the same device.
 */
export function profilePartitionedKey({ request }: { request: { url: string; headers: { get(name: string): string | null } } }): string {
  const key = new URL(request.url);
  key.searchParams.set('x-profile', request.headers.get('X-Profile-Id') ?? 'none');
  return key.href;
}
