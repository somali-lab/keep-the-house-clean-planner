import { AiProviderError, type AiProvider, type CompleteJsonRequest } from '../provider.ts';

/**
 * A responder answers one request name. Fixed strings are served in order
 * (the last one repeats); functions get the 0-based attempt number.
 */
export type MockResponder = string[] | ((request: CompleteJsonRequest, attempt: number) => string);
export type MockResponders = Record<string, MockResponder>;

export interface MockProviderOptions {
  responders: MockResponders;
  /** Answer the first request of every name with invalid JSON, to exercise the re-prompt path. */
  invalidFirst?: boolean;
}

export const MOCK_INVALID_RESPONSE = 'this is not json';

/** Deterministic provider for tests and demos; never calls the network. */
export class MockProvider implements AiProvider {
  readonly type = 'mock' as const;
  readonly requests: CompleteJsonRequest[] = [];
  private readonly responders: MockResponders;
  private readonly invalidFirst: boolean;
  private readonly attempts = new Map<string, number>();

  constructor(options: MockProviderOptions) {
    this.responders = options.responders;
    this.invalidFirst = options.invalidFirst ?? false;
  }

  async completeJson(request: CompleteJsonRequest): Promise<string> {
    this.requests.push(request);
    const attempt = this.attempts.get(request.name) ?? 0;
    this.attempts.set(request.name, attempt + 1);

    if (this.invalidFirst && attempt === 0) return MOCK_INVALID_RESPONSE;

    const responder = this.responders[request.name];
    if (!responder) throw new AiProviderError(`Mock provider has no response for "${request.name}"`);
    if (typeof responder === 'function') return responder(request, attempt);
    const index = Math.min(attempt - (this.invalidFirst ? 1 : 0), responder.length - 1);
    const answer = responder[Math.max(0, index)];
    if (answer === undefined) throw new AiProviderError(`Mock provider has no response for "${request.name}"`);
    return answer;
  }
}
