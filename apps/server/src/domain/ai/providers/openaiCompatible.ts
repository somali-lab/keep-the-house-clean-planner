import { AiProviderError, DEFAULT_TIMEOUT_MS, type AiProvider, type CompleteJsonRequest } from '../provider.ts';

export interface OpenAiCompatibleOptions {
  /** Base URL including the version path, e.g. https://api.openai.com/v1 */
  endpoint: string;
  model: string;
  apiKey?: string;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
}

/** Any endpoint that speaks the OpenAI Chat Completions API, asked for a JSON object. */
export class OpenAiCompatibleProvider implements AiProvider {
  readonly type = 'openai-compatible' as const;
  private readonly options: OpenAiCompatibleOptions;

  constructor(options: OpenAiCompatibleOptions) {
    this.options = options;
  }

  async completeJson(request: CompleteJsonRequest): Promise<string> {
    const { endpoint, model, apiKey, timeoutMs = DEFAULT_TIMEOUT_MS } = this.options;
    const doFetch = this.options.fetchImpl ?? fetch;
    let res: Response;
    try {
      res = await doFetch(`${endpoint.replace(/\/+$/, '')}/chat/completions`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(apiKey ? { Authorization: `Bearer ${apiKey}` } : {}),
        },
        body: JSON.stringify({
          model,
          messages: [
            { role: 'system', content: request.system },
            { role: 'user', content: request.user },
          ],
          response_format: { type: 'json_object' },
        }),
        signal: AbortSignal.timeout(timeoutMs),
      });
    } catch {
      throw new AiProviderError('AI provider could not be reached');
    }
    if (!res.ok) throw new AiProviderError(`AI provider returned HTTP ${res.status}`);

    const body = (await res.json().catch(() => null)) as { choices?: { message?: { content?: unknown } }[] } | null;
    const content = body?.choices?.[0]?.message?.content;
    if (typeof content !== 'string' || content.length === 0) {
      throw new AiProviderError('AI provider returned no content');
    }
    return content;
  }
}
