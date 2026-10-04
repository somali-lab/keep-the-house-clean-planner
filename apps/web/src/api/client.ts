/** A request the server answered with a problem (RFC 9457): the HTTP status, the code after `urn:huishoudplanner:problem:`, the detail. */
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
