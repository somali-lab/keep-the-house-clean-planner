import type { ApiWarning } from '@huishoudplanner/shared/schemas/api';

export interface ApiResult<T> {
  data: T;
  warnings: ApiWarning[];
}

export class ApiRequestError extends Error {
  readonly status: number;
  readonly code: string;
  readonly details: unknown;

  constructor(status: number, code: string, message?: string, details?: unknown) {
    super(message ?? code);
    this.name = 'ApiRequestError';
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

export interface ApiClientOptions {
  baseUrl?: string;
  /** Active profile id; sent as X-Profile-Id on every request. */
  getProfileId: () => string | null;
  fetchImpl?: typeof fetch;
}

type Method = 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function createApiClient(options: ApiClientOptions) {
  const baseUrl = options.baseUrl ?? '';
  const doFetch = options.fetchImpl ?? ((...args: Parameters<typeof fetch>) => fetch(...args));

  async function request<T>(method: Method, path: string, body?: unknown): Promise<ApiResult<T>> {
    const headers: Record<string, string> = { 'X-Client': 'web', Accept: 'application/json' };
    const profileId = options.getProfileId();
    if (profileId) headers['X-Profile-Id'] = profileId;
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    const res = await doFetch(`${baseUrl}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });

    const text = await res.text();
    let json: unknown = undefined;
    if (text) {
      try {
        json = JSON.parse(text);
      } catch {
        json = undefined;
      }
    }

    if (!res.ok) {
      const err = isRecord(json) ? json : {};
      throw new ApiRequestError(
        res.status,
        typeof err.code === 'string' ? err.code : 'http_error',
        typeof err.message === 'string' ? err.message : res.statusText,
        err.details,
      );
    }

    const warnings = isRecord(json) && Array.isArray(json.warnings) ? (json.warnings as ApiWarning[]) : [];
    return { data: json as T, warnings };
  }

  return {
    request,
    get: <T>(path: string) => request<T>('GET', path),
    post: <T>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
    patch: <T>(path: string, body: unknown) => request<T>('PATCH', path, body),
    put: <T>(path: string, body: unknown) => request<T>('PUT', path, body),
    delete: <T>(path: string) => request<T>('DELETE', path),
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
