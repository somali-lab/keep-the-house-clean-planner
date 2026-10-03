// One-off export of the golden vectors (docs/plans/dotnet-rewrite.md section 7.2).
// Usage: npm run export:vectors
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { buildVectors, serializeVectors, VECTORS_DIR } from './vectors.ts';

await mkdir(VECTORS_DIR, { recursive: true });
for (const file of buildVectors()) {
  await writeFile(join(VECTORS_DIR, `${file.module}.json`), serializeVectors(file));
  console.log(`${file.module}.json: ${file.cases.length} cases`);
}
