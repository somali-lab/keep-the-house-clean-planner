import { describe, expect, it, vi } from 'vitest';
import { getSettings } from '../src/data/settings.ts';
import { createAiProvider } from '../src/domain/ai/factory.ts';
import { AiConfigError, AiDisabledError, AiProviderError, type CompleteJsonRequest } from '../src/domain/ai/provider.ts';
import { MOCK_INVALID_RESPONSE, MockProvider } from '../src/domain/ai/providers/mock.ts';
import { NoneProvider } from '../src/domain/ai/providers/none.ts';
import { OllamaProvider } from '../src/domain/ai/providers/ollama.ts';
import { OpenAiCompatibleProvider } from '../src/domain/ai/providers/openaiCompatible.ts';
import { createTestApp } from './helpers/testApp.ts';

const SECRET = 'sk-test-super-secret-key';

const request: CompleteJsonRequest = {
  name: 'plan-proposal',
  system: 'Je bent een planner.',
  user: '{"tasks":[]}',
  schema: { type: 'object' },
};

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function captured(fetchMock: ReturnType<typeof vi.fn>) {
  const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
  return { url, init, body: JSON.parse(String(init.body)), headers: init.headers as Record<string, string> };
}

describe('none provider', () => {
  it('rejects every request with 503 ai_disabled', async () => {
    const error = await new NoneProvider().completeJson(request).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(AiDisabledError);
    expect(error).toMatchObject({ statusCode: 503, code: 'ai_disabled' });
  });

  it('is what a freshly seeded app uses, so everything else works without AI', async () => {
    const t = await createTestApp();
    try {
      const settings = await getSettings(t.db);
      expect(settings?.aiProvider).toEqual({ type: 'none' });
      expect(createAiProvider(settings!.aiProvider)).toBeInstanceOf(NoneProvider);
    } finally {
      await t.close();
    }
  });
});

describe('mock provider', () => {
  it('serves fixture answers per request name, in order, repeating the last', async () => {
    const provider = new MockProvider({ responders: { 'plan-proposal': ['{"a":1}', '{"a":2}'] } });
    expect(await provider.completeJson(request)).toBe('{"a":1}');
    expect(await provider.completeJson(request)).toBe('{"a":2}');
    expect(await provider.completeJson(request)).toBe('{"a":2}');
    expect(provider.requests).toHaveLength(3);
  });

  it('can answer invalid JSON first, to exercise re-prompting', async () => {
    const provider = new MockProvider({ responders: { 'plan-proposal': ['{"ok":true}'] }, invalidFirst: true });
    expect(await provider.completeJson(request)).toBe(MOCK_INVALID_RESPONSE);
    expect(await provider.completeJson(request)).toBe('{"ok":true}');
  });

  it('supports function responders with the attempt number, and fails for unknown names', async () => {
    const provider = new MockProvider({ responders: { explain: (req, attempt) => JSON.stringify({ name: req.name, attempt }) } });
    expect(await provider.completeJson({ ...request, name: 'explain' })).toBe('{"name":"explain","attempt":0}');
    expect(await provider.completeJson({ ...request, name: 'explain' })).toBe('{"name":"explain","attempt":1}');
    await expect(provider.completeJson({ ...request, name: 'other' })).rejects.toBeInstanceOf(AiProviderError);
  });
});

describe('openai-compatible provider', () => {
  it('posts a chat completion asking for a JSON object and returns the message content', async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { choices: [{ message: { content: '{"slots":[]}' } }] }));
    const provider = new OpenAiCompatibleProvider({ endpoint: 'https://llm.example/v1/', model: 'gpt-x', apiKey: SECRET, fetchImpl: fetchMock });

    expect(await provider.completeJson(request)).toBe('{"slots":[]}');
    const { url, init, body, headers } = captured(fetchMock);
    expect(url).toBe('https://llm.example/v1/chat/completions');
    expect(init.method).toBe('POST');
    expect(headers).toMatchObject({ 'Content-Type': 'application/json', Authorization: `Bearer ${SECRET}` });
    expect(body).toEqual({
      model: 'gpt-x',
      messages: [
        { role: 'system', content: 'Je bent een planner.' },
        { role: 'user', content: '{"tasks":[]}' },
      ],
      response_format: { type: 'json_object' },
    });
  });

  it('turns HTTP errors and empty answers into ai_provider_error without leaking the key or body', async () => {
    const failing = new OpenAiCompatibleProvider({
      endpoint: 'https://llm.example/v1',
      model: 'gpt-x',
      apiKey: SECRET,
      fetchImpl: vi.fn(async () => jsonResponse(401, { error: `bad key ${SECRET}` })),
    });
    const error = (await failing.completeJson(request).catch((e: unknown) => e)) as AiProviderError;
    expect(error).toMatchObject({ statusCode: 502, code: 'ai_provider_error', message: 'AI provider returned HTTP 401' });
    expect(JSON.stringify({ ...error, message: error.message })).not.toContain(SECRET);

    const empty = new OpenAiCompatibleProvider({
      endpoint: 'https://llm.example/v1',
      model: 'gpt-x',
      fetchImpl: vi.fn(async () => jsonResponse(200, { choices: [] })),
    });
    await expect(empty.completeJson(request)).rejects.toThrow('AI provider returned no content');

    const unreachable = new OpenAiCompatibleProvider({
      endpoint: 'https://llm.example/v1',
      model: 'gpt-x',
      fetchImpl: vi.fn(async () => {
        throw new Error(`connect ECONNREFUSED ${SECRET}`);
      }),
    });
    const offline = (await unreachable.completeJson(request).catch((e: unknown) => e)) as Error;
    expect(offline.message).toBe('AI provider could not be reached');
  });

  it('omits the Authorization header without a key', async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { choices: [{ message: { content: '{}' } }] }));
    await new OpenAiCompatibleProvider({ endpoint: 'http://local/v1', model: 'm', fetchImpl: fetchMock }).completeJson(request);
    expect(captured(fetchMock).headers).not.toHaveProperty('Authorization');
  });
});

describe('ollama provider', () => {
  it('calls /api/chat with the JSON schema, deterministic output and no streaming', async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { message: { role: 'assistant', content: '{"x":1}' }, done: true }));
    const provider = new OllamaProvider({ endpoint: 'http://ollama:11434/', model: 'llama3.2', fetchImpl: fetchMock });

    expect(await provider.completeJson(request)).toBe('{"x":1}');
    const { url, body, headers } = captured(fetchMock);
    expect(url).toBe('http://ollama:11434/api/chat');
    expect(headers).not.toHaveProperty('Authorization');
    expect(body).toEqual({
      model: 'llama3.2',
      messages: [
        { role: 'system', content: 'Je bent een planner.' },
        { role: 'user', content: '{"tasks":[]}' },
      ],
      format: request.schema,
      stream: false,
      think: false,
      options: { temperature: 0, num_predict: 8192 },
    });
  });

  it('reports HTTP errors', async () => {
    const provider = new OllamaProvider({ endpoint: 'http://ollama:11434', model: 'm', fetchImpl: vi.fn(async () => jsonResponse(500, {})) });
    await expect(provider.completeJson(request)).rejects.toThrow('Ollama returned HTTP 500');
  });

  it('converts fixed tuples to a schema supported by Ollama', async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { message: { content: '{"rationale":["a","b","c","d"]}' } }));
    const provider = new OllamaProvider({ endpoint: 'http://ollama:11434', model: 'gemma4', fetchImpl: fetchMock });
    await provider.completeJson({
      ...request,
      schema: {
        type: 'object',
        properties: {
          rationale: {
            type: 'array',
            prefixItems: Array.from({ length: 4 }, () => ({ type: 'string', minLength: 1 })),
            items: false,
            minItems: 4,
            maxItems: 4,
          },
        },
      },
    });

    const { body } = captured(fetchMock);
    expect(body.format.properties.rationale).toEqual({
      type: 'array',
      items: { type: 'string', minLength: 1 },
      minItems: 4,
      maxItems: 4,
    });
  });
});

describe('createAiProvider', () => {
  it('selects the provider from settings', () => {
    expect(createAiProvider({ type: 'none' }).type).toBe('none');
    expect(createAiProvider({ type: 'mock' }).type).toBe('mock');
    expect(createAiProvider({ type: 'openai-compatible', endpoint: 'https://llm.example/v1', model: 'm' }, { apiKey: SECRET }).type).toBe(
      'openai-compatible',
    );
    expect(createAiProvider({ type: 'ollama', model: 'llama3.2' }).type).toBe('ollama');
    expect(createAiProvider({ type: 'anthropic' }, { apiKey: SECRET }).type).toBe('anthropic');
  });

  it('reports missing configuration as ai_misconfigured, never including the key', () => {
    const cases: [Parameters<typeof createAiProvider>[0], Parameters<typeof createAiProvider>[1]][] = [
      [{ type: 'anthropic' }, {}],
      [{ type: 'openai-compatible', model: 'm' }, { apiKey: SECRET }],
      [{ type: 'ollama' }, {}],
    ];
    for (const [settings, options] of cases) {
      let error: unknown;
      try {
        createAiProvider(settings, options);
      } catch (e) {
        error = e;
      }
      expect(error, settings.type).toBeInstanceOf(AiConfigError);
      expect(error).toMatchObject({ statusCode: 503, code: 'ai_misconfigured' });
      expect((error as Error).message).not.toContain(SECRET);
    }
  });
});
