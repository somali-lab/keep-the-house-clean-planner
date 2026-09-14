import { resolve } from 'node:path';
import { ESLint } from 'eslint';
import { describe, expect, it } from 'vitest';

const ROOT = resolve(import.meta.dirname, '../../..');
const CODE = `declare const collection: { updateOne(f: object, u: object): Promise<void> };
export async function probe(): Promise<void> {
  await collection.updateOne({}, { $set: { a: 1 } });
}
`;

async function restrictedErrors(relativePath: string) {
  const eslint = new ESLint({ cwd: ROOT });
  const [result] = await eslint.lintText(CODE, { filePath: resolve(ROOT, relativePath) });
  return (result?.messages ?? []).filter((m) => m.ruleId === 'no-restricted-syntax');
}

describe('mongo write lint rule', () => {
  it.each(['apps/server/src/routes/probe.ts', 'apps/server/src/domain/probe.ts', 'apps/server/test/probe.test.ts'])(
    'forbids write operations in %s',
    async (path) => {
      const errors = await restrictedErrors(path);
      expect(errors).toHaveLength(1);
      expect(errors[0]?.message).toContain('apps/server/src/data/');
    },
  );

  it('allows write operations inside the data layer', async () => {
    expect(await restrictedErrors('apps/server/src/data/probe.ts')).toHaveLength(0);
  });
});
