import { describe, expect, it, vi } from 'vitest';
import { createAiProvider } from '../src/domain/ai/factory.ts';
import { AiProviderError, type CompleteJsonRequest } from '../src/domain/ai/provider.ts';
import {
  ANTHROPIC_MAX_TOKENS,
  AnthropicProvider,
  DEFAULT_ANTHROPIC_MODEL,
  type AnthropicCreateParams,
  type AnthropicMessagesClient,
} from '../src/domain/ai/providers/anthropic.ts';

const SECRET = 'sk-ant-test-super-secret';

const request: CompleteJsonRequest = {
  name: 'plan-proposal',
  system: 'Je bent een planner. Antwoord met JSON.',
  user: '{"tasks":[]}',
  schema: { type: 'object' },
};

function fakeClient(
  impl: (params: AnthropicCreateParams) => Promise<{ content: { type: string; text?: unknown }[] }>,
): { client: AnthropicMessagesClient; create: ReturnType<typeof vi.fn> } {
  const create = vi.fn(impl);
  return { client: { messages: { create } }, create };
}

describe('anthropic provider', () => {
  it('sends system and user prompt to the Messages API without sampling parameters', async () => {
    const { client, create } = fakeClient(async () => ({ content: [{ type: 'text', text: '{"slots":[]}' }] }));
    const provider = new AnthropicProvider({ model: 'claude-sonnet-5', client });

    expect(await provider.completeJson(request)).toBe('{"slots":[]}');
    expect(create).toHaveBeenCalledTimes(1);
    const params = create.mock.calls[0]![0] as AnthropicCreateParams & Record<string, unknown>;
    expect(params).toEqual({
      model: 'claude-sonnet-5',
      max_tokens: ANTHROPIC_MAX_TOKENS,
      system: 'Je bent een planner. Antwoord met JSON.',
      messages: [{ role: 'user', content: '{"tasks":[]}' }],
    });
    expect(params).not.toHaveProperty('temperature');
  });

  it('joins text blocks and ignores other block types', async () => {
    const { client } = fakeClient(async () => ({
      content: [
        { type: 'thinking' },
        { type: 'text', text: '{"a":' },
        { type: 'text', text: '1}' },
      ],
    }));
    expect(await new AnthropicProvider({ model: 'm', client }).completeJson(request)).toBe('{"a":1}');
  });

  it('reports API errors by status only, without leaking the key or SDK message', async () => {
    const { client } = fakeClient(async () => {
      throw Object.assign(new Error(`invalid x-api-key ${SECRET}`), { status: 401 });
    });
    const error = (await new AnthropicProvider({ model: 'm', client }).completeJson(request).catch((e: unknown) => e)) as AiProviderError;
    expect(error).toBeInstanceOf(AiProviderError);
    expect(error).toMatchObject({ statusCode: 502, code: 'ai_provider_error', message: 'Anthropic API returned HTTP 401' });
    expect(error.message).not.toContain(SECRET);

    const { client: offline } = fakeClient(async () => {
      throw new Error('socket hang up');
    });
    await expect(new AnthropicProvider({ model: 'm', client: offline }).completeJson(request)).rejects.toThrow(
      'Anthropic API could not be reached',
    );
  });

  it('fails clearly when the answer has no text', async () => {
    const { client } = fakeClient(async () => ({ content: [{ type: 'text', text: '   ' }] }));
    await expect(new AnthropicProvider({ model: 'm', client }).completeJson(request)).rejects.toThrow(
      'Anthropic API returned no content',
    );
  });

  it('uses the model from settings, or the default, and keeps the key off the provider object', async () => {
    const { client, create } = fakeClient(async () => ({ content: [{ type: 'text', text: '{}' }] }));
    await createAiProvider({ type: 'anthropic' }, { anthropicClient: client }).completeJson(request);
    expect((create.mock.calls[0]![0] as AnthropicCreateParams).model).toBe(DEFAULT_ANTHROPIC_MODEL);

    await createAiProvider({ type: 'anthropic', model: 'claude-haiku-4-5-20251001' }, { anthropicClient: client }).completeJson(request);
    expect((create.mock.calls[1]![0] as AnthropicCreateParams).model).toBe('claude-haiku-4-5-20251001');

    // Real SDK client construction does no network I/O; the key must not be reachable via JSON.
    const real = new AnthropicProvider({ apiKey: SECRET, model: DEFAULT_ANTHROPIC_MODEL });
    expect(real.type).toBe('anthropic');
    expect(JSON.stringify(real)).not.toContain(SECRET);
  });
});
