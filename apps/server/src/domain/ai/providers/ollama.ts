import { DEFAULT_AI_TIMEOUT_SECONDS } from '@huishoudplanner/shared';
import { AiProviderError, type AiProvider, type CompleteJsonRequest } from '../provider.ts';

export const DEFAULT_OLLAMA_ENDPOINT = 'http://localhost:11434';

export interface OllamaOptions {
  endpoint: string;
  model: string;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
}

/**
 * Ollama's grammar converter does not accept the JSON Schema 2020-12 tuple
 * representation (`prefixItems` plus `items: false`). Keep the fixed length,
 * but express the item type as a regular array schema it does understand.
 */
function toOllamaSchema(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(toOllamaSchema);
  if (value === null || typeof value !== 'object') return value;

  const source = value as Record<string, unknown>;
  const converted = Object.fromEntries(Object.entries(source).map(([key, child]) => [key, toOllamaSchema(child)]));
  if (Array.isArray(source.prefixItems) && source.prefixItems.length > 0) {
    const alternatives = source.prefixItems.map(toOllamaSchema);
    const first = JSON.stringify(alternatives[0]);
    converted.items = alternatives.every((item) => JSON.stringify(item) === first) ? alternatives[0] : { anyOf: alternatives };
    delete converted.prefixItems;
  } else if (source.items === false) {
    delete converted.items;
  }
  return converted;
}

/** Local Ollama via POST /api/chat with schema-constrained structured output. */
export class OllamaProvider implements AiProvider {
  readonly type = 'ollama' as const;
  private readonly options: OllamaOptions;

  constructor(options: OllamaOptions) {
    this.options = options;
  }

  async completeJson(request: CompleteJsonRequest): Promise<string> {
    const { endpoint, model, timeoutMs = DEFAULT_AI_TIMEOUT_SECONDS * 1000 } = this.options;
    const doFetch = this.options.fetchImpl ?? fetch;
    let res: Response;
    try {
      res = await doFetch(`${endpoint.replace(/\/+$/, '')}/api/chat`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          model,
          messages: [
            { role: 'system', content: request.system },
            { role: 'user', content: request.user },
          ],
          // Ollama accepts a JSON Schema here. This constrains generation much
          // more reliably than the loose `format: "json"` mode, especially for
          // the large array of slots in a four-week plan.
          format: toOllamaSchema(request.schema),
          stream: false,
          think: false,
          options: {
            temperature: 0,
            num_predict: 8192,
          },
        }),
        signal: AbortSignal.timeout(timeoutMs),
      });
    } catch (error) {
      if (error instanceof Error && (error.name === 'TimeoutError' || error.name === 'AbortError')) {
        throw new AiProviderError(`Ollama timed out after ${Math.round(timeoutMs / 1000)} seconds`);
      }
      throw new AiProviderError('Ollama could not be reached');
    }
    if (!res.ok) throw new AiProviderError(`Ollama returned HTTP ${res.status}`);

    const body = (await res.json().catch(() => null)) as { message?: { content?: unknown } } | null;
    const content = body?.message?.content;
    if (typeof content !== 'string' || content.length === 0) {
      throw new AiProviderError('Ollama returned no content');
    }
    return content;
  }
}
