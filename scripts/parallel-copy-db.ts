#!/usr/bin/env node
/**
 * Copies the live database into the database of the parallel run: mongodump of `huishoudplanner`, mongorestore as
 * `huishoudplanner_next`, on the same MongoDB instance, through `docker compose exec` (docs/PARALLEL-RUN.md, plan section 10).
 *
 * Safety rails (scripts/lib/parallelCopy.ts): the source is only read; the target must be `huishoudplanner_next` unless
 * --force, is never the source and never the live database; app-next must be stopped; the plan is printed and confirmed.
 * Run `node scripts/parallel-copy-db.ts --help` for the options and prerequisites.
 */
import { spawn, spawnSync } from 'node:child_process';
import { createInterface } from 'node:readline/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  buildCommands,
  checkRails,
  compareCounts,
  MONGO_SERVICE,
  NEXT_SERVICE,
  parseArgs,
  UsageError,
  USAGE,
  type Counts,
} from './lib/parallelCopy.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

function docker(args: string[]): { status: number | null; stdout: string; stderr: string } {
  const result = spawnSync('docker', args, { cwd: ROOT, encoding: 'utf8' });
  if (result.error) throw new Error(`docker is not available (${result.error.message})`);
  return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
}

function counts(args: string[], what: string): Counts {
  const result = docker(args);
  if (result.status !== 0)
    throw new Error(`Could not count the documents of ${what}:\n${result.stderr || result.stdout}`);
  const line = result.stdout.trim().split(/\r?\n/).filter(Boolean).pop() ?? '{}';
  return JSON.parse(line) as Counts;
}

const total = (c: Counts) => Object.values(c).reduce((a, b) => a + b, 0);

function describe(name: string, c: Counts): string {
  const names = Object.keys(c);
  if (names.length === 0) return `${name}: no collections (does not exist or is empty)`;
  return `${name}: ${names.length} collections, ${total(c)} documents (${names.map((n) => `${n} ${c[n]}`).join(', ')})`;
}

async function confirm(question: string): Promise<boolean> {
  if (!process.stdin.isTTY) return false;
  const rl = createInterface({ input: process.stdin, output: process.stdout });
  try {
    return (
      (await rl.question(`${question} Type "yes" to continue: `)).trim().toLowerCase() === 'yes'
    );
  } finally {
    rl.close();
  }
}

/** Streams the dump of the source into the restore of the target and waits for both. */
function pipeCopy(dump: string[], restore: string[]): Promise<void> {
  return new Promise((resolvePromise, reject) => {
    const producer = spawn('docker', dump, { cwd: ROOT, stdio: ['ignore', 'pipe', 'inherit'] });
    const consumer = spawn('docker', restore, { cwd: ROOT, stdio: ['pipe', 'inherit', 'inherit'] });
    producer.stdout.pipe(consumer.stdin);
    let exits = 0;
    const codes: Record<string, number | null> = {};
    const done = (who: string) => (code: number | null) => {
      codes[who] = code;
      if (++exits < 2) return;
      if (codes.mongodump === 0 && codes.mongorestore === 0) resolvePromise();
      else
        reject(
          new Error(
            `mongodump exited with ${codes.mongodump}, mongorestore with ${codes.mongorestore}`,
          ),
        );
    };
    producer.on('error', reject);
    consumer.on('error', reject);
    producer.on('exit', done('mongodump'));
    consumer.on('exit', done('mongorestore'));
  });
}

async function main(): Promise<number> {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    console.log(USAGE);
    return 0;
  }
  const refusals = checkRails(options);
  if (refusals.length > 0) {
    console.error(refusals.map((r) => `Refusing: ${r}`).join('\n'));
    return 2;
  }
  const commands = buildCommands(options);

  if (options.dryRun) {
    console.log(
      `Dry run: ${options.source} -> ${options.target}. Nothing is run. The commands would be:`,
    );
    for (const [label, args] of [
      ['count source', commands.count(options.source)],
      ['count target', commands.count(options.target)],
      ['drop target', commands.dropTarget],
      ['dump source | restore target', null],
    ] as const) {
      if (args)
        console.log(
          `  ${label}: docker ${args.map((a) => (/\s/.test(a) ? JSON.stringify(a) : a)).join(' ')}`,
        );
      else
        console.log(
          `  ${label}: docker ${commands.dump.join(' ')}  |  docker ${commands.restore.join(' ')}`,
        );
    }
    return 0;
  }

  const running = docker(commands.running);
  if (running.status !== 0) throw new Error(`docker compose ps failed:\n${running.stderr}`);
  const services = running.stdout.split(/\r?\n/).map((s) => s.trim());
  if (!services.includes(MONGO_SERVICE))
    throw new Error(
      `The "${MONGO_SERVICE}" service is not running. Start it first: docker compose up -d ${MONGO_SERVICE}`,
    );
  if (services.includes(NEXT_SERVICE)) {
    throw new Error(
      `"${NEXT_SERVICE}" is running and holds the target database open. Stop it first: docker compose --profile parallel stop ${NEXT_SERVICE}`,
    );
  }

  const before = counts(commands.count(options.source), options.source);
  if (total(before) === 0)
    throw new Error(
      `The source database "${options.source}" has no documents; refusing to replace the target with nothing.`,
    );
  const existing = counts(commands.count(options.target), options.target);

  console.log('Plan');
  console.log(`  read     ${describe(options.source, before)}`);
  console.log(`  replace  ${describe(options.target, existing)}`);
  console.log(
    `  steps    drop "${options.target}", then mongodump "${options.source}" | mongorestore as "${options.target}" (inside the ${MONGO_SERVICE} service)`,
  );
  console.log(`  untouched  "${options.source}" is only read; no other database is touched`);

  if (!options.yes && !(await confirm(`\nThis REPLACES the database "${options.target}".`))) {
    console.error(
      process.stdin.isTTY
        ? 'Not confirmed, nothing was done.'
        : 'No terminal to confirm on and no --yes given, nothing was done.',
    );
    return 1;
  }

  const drop = docker(commands.dropTarget);
  if (drop.status !== 0)
    throw new Error(`Dropping the target failed:\n${drop.stderr || drop.stdout}`);
  console.log(`\nDropped "${options.target}". Copying ...`);
  await pipeCopy(commands.dump, commands.restore);

  const after = counts(commands.count(options.target), options.target);
  const sourceAfter = counts(commands.count(options.source), options.source);
  const sourceMoved = compareCounts(before, sourceAfter);
  const problems = compareCounts(before, after);
  console.log(`\n${describe(options.target, after)}`);
  if (problems.length > 0) {
    console.error(
      `\nThe copy differs from the source as counted before it:\n${problems.map((p) => `  - ${p}`).join('\n')}`,
    );
    if (sourceMoved.length > 0) {
      // The household kept using the live app while the dump ran, so the counts are not comparable. Not a failure.
      console.error(
        `\nThe source changed while it was copied (${sourceMoved.join('; ')}), so this is expected. Run the copy again at a quiet moment if an exact copy is needed.`,
      );
      return 0;
    }
    return 1;
  }
  console.log('Copy complete: every collection has the same number of documents as the source.');
  return 0;
}

if (import.meta.main) {
  main().then(
    (code) => process.exit(code),
    (error: unknown) => {
      console.error(
        error instanceof UsageError
          ? `${error.message}\n\n${USAGE}`
          : error instanceof Error
            ? error.message
            : String(error),
      );
      process.exit(error instanceof UsageError ? 2 : 1);
    },
  );
}
