import type { Reader } from './parity.ts';

/**
 * A {@link Reader} over HTTP that only ever issues GET requests: the parity run is read-only against both applications, and this
 * is the one place that sends anything. `root` is the API root (for example `http://127.0.0.1:3000/api`).
 */
export function httpReader(
  root: string,
  options: { timeoutMs?: number; profileId?: string; fetchImpl?: typeof fetch } = {},
): Reader {
  const base = root.replace(/\/+$/, '');
  const fetchImpl = options.fetchImpl ?? fetch;
  return {
    async get(path) {
      const url = `${base}${path}`;
      let response: Response;
      try {
        response = await fetchImpl(url, {
          method: 'GET',
          headers: {
            accept: 'application/json',
            ...(options.profileId ? { 'x-profile-id': options.profileId } : {}),
          },
          signal: AbortSignal.timeout(options.timeoutMs ?? 30_000),
        });
      } catch (error) {
        throw new Error(
          `GET ${url} failed: ${error instanceof Error ? error.message : String(error)}`,
          { cause: error },
        );
      }
      const text = await response.text();
      if (!response.ok)
        throw new Error(`GET ${url} answered ${response.status}: ${text.slice(0, 200)}`);
      try {
        return JSON.parse(text);
      } catch {
        throw new Error(`GET ${url} did not answer JSON`);
      }
    },
  };
}
