import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { buildVectors, MODULES, serializeVectors, VECTORS_DIR } from '../../../scripts/vectors.ts';
import * as cycle from './cycle.ts';
import * as due from './due.ts';
import * as time from './time.ts';

const SOURCES: Record<string, Record<string, unknown>> = { time, cycle, due };

/** Exported values that are constants or types of the module, not functions to port. */
const EXPORTED_FUNCTIONS = (source: Record<string, unknown>) =>
  Object.entries(source)
    .filter(([, value]) => typeof value === 'function')
    .map(([name]) => name)
    .sort();

describe('golden vectors', () => {
  const generated = buildVectors();

  it.each(generated.map((file) => [file.module, file] as const))(
    '%s.json is up to date (run `npm run export:vectors`)',
    (module, file) => {
      const checkedIn = readFileSync(join(VECTORS_DIR, `${module}.json`), 'utf8');
      expect(checkedIn).toBe(serializeVectors(file));
    },
  );

  it('covers every function of the exported modules', () => {
    for (const { module, functions } of MODULES) {
      const source = SOURCES[module];
      expect(source, `no source module registered for ${module}`).toBeDefined();
      expect(Object.keys(functions).sort()).toEqual(EXPORTED_FUNCTIONS(source ?? {}));
    }
  });

  it('records every case with exactly one of expected or throws', () => {
    for (const file of generated) {
      expect(file.cases.length).toBeGreaterThan(0);
      const names = new Set<string>();
      for (const c of file.cases) {
        expect(Number(c.expected !== undefined) + Number(c.throws !== undefined), c.name).toBe(1);
        const id = `${c.function}: ${c.name}`;
        expect(names.has(id), `duplicate case ${id}`).toBe(false);
        names.add(id);
      }
    }
  });

  it('keeps the DST, week-year and leap-day scenarios the ports rely on', () => {
    const flat = JSON.stringify(generated);
    for (const marker of ['2026-03-29', '2026-10-25', '2026-W53', '2028-02-29']) {
      expect(flat).toContain(marker);
    }
  });
});
