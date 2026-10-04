/**
 * Pure parts of scripts/parallel-copy-db.ts: argument parsing, the safety rails and the commands that are run. Kept apart so
 * the rails can be unit tested without Docker (docs/plans/dotnet-rewrite.md section 10, step 2).
 */

/** The database of the live Node application. */
export const DEFAULT_SOURCE_DB = 'huishoudplanner';
/** The only database this tool restores into unless `--force` is given. */
export const DEFAULT_TARGET_DB = 'huishoudplanner_next';
/** The compose service that runs MongoDB. */
export const MONGO_SERVICE = 'mongo';
/** The compose service of the .NET application of the parallel run. */
export const NEXT_SERVICE = 'app-next';

export interface CopyOptions {
  source: string;
  target: string;
  /** Allows a target other than {@link DEFAULT_TARGET_DB}. Never allows target = source. */
  force: boolean;
  /** Skips the confirmation prompt. */
  yes: boolean;
  /** Prints the plan and the commands and does nothing else. */
  dryRun: boolean;
  /** Compose project name (`-p`); omitted means the one of the compose file. */
  project: string | undefined;
  /** Compose file(s) (`-f`); omitted means docker-compose.yml. */
  files: string[];
  help: boolean;
}

export class UsageError extends Error {}

const DB_NAME = /^[A-Za-z][A-Za-z0-9_]{0,62}$/;

export function parseArgs(argv: string[]): CopyOptions {
  const options: CopyOptions = {
    source: DEFAULT_SOURCE_DB,
    target: DEFAULT_TARGET_DB,
    force: false,
    yes: false,
    dryRun: false,
    project: undefined,
    files: [],
    help: false,
  };
  const value = (flag: string, index: number): string => {
    const next = argv[index + 1];
    if (next === undefined || next.startsWith('--')) throw new UsageError(`${flag} needs a value`);
    return next;
  };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]!;
    const [flag, inline] =
      arg.startsWith('--') && arg.includes('=')
        ? [arg.slice(0, arg.indexOf('=')), arg.slice(arg.indexOf('=') + 1)]
        : [arg, undefined];
    const take = (): string => {
      if (inline !== undefined) return inline;
      const v = value(flag, i);
      i++;
      return v;
    };
    switch (flag) {
      case '--source':
        options.source = take();
        break;
      case '--target':
        options.target = take();
        break;
      case '--project':
      case '-p':
        options.project = take();
        break;
      case '--file':
      case '-f':
        options.files.push(take());
        break;
      case '--force':
        options.force = true;
        break;
      case '--yes':
      case '-y':
        options.yes = true;
        break;
      case '--dry-run':
        options.dryRun = true;
        break;
      case '--help':
      case '-h':
        options.help = true;
        break;
      default:
        throw new UsageError(`Unknown argument: ${arg}`);
    }
  }
  return options;
}

/** The refusals, as human-readable problems; an empty list means the copy may go ahead. */
export function checkRails(options: Pick<CopyOptions, 'source' | 'target' | 'force'>): string[] {
  const problems: string[] = [];
  for (const [label, name] of [
    ['source', options.source],
    ['target', options.target],
  ] as const) {
    if (!DB_NAME.test(name))
      problems.push(
        `The ${label} database name "${name}" is not a plain MongoDB database name (letters, digits, underscore).`,
      );
  }
  // Names are compared case-insensitively so that "Huishoudplanner" cannot slip past as another database.
  if (options.source.toLowerCase() === options.target.toLowerCase())
    problems.push(
      'The source and the target are the same database. Nothing is copied over itself, not even with --force.',
    );
  if (options.target !== DEFAULT_TARGET_DB && !options.force) {
    problems.push(
      `The target database is "${options.target}", not "${DEFAULT_TARGET_DB}". Restoring replaces the target completely; pass --force only if that is really the database to replace.`,
    );
  }
  if (options.target.toLowerCase() === DEFAULT_SOURCE_DB) {
    problems.push(
      `"${DEFAULT_SOURCE_DB}" is the live database of the Node application and is never a restore target, not even with --force.`,
    );
  }
  return problems;
}

/** `docker compose [-p project] [-f file ...]` as an argument list for `docker`. */
export function composeBase(options: Pick<CopyOptions, 'project' | 'files'>): string[] {
  return [
    'compose',
    ...(options.project ? ['-p', options.project] : []),
    ...options.files.flatMap((f) => ['-f', f]),
  ];
}

/** The in-container address of MongoDB; tools run inside the mongo service, so no host port is needed. */
const LOCAL = ['--host', '127.0.0.1', '--port', '27017'];

export interface CopyCommands {
  /** Read-only: counts the documents per collection of a database, printed as JSON. */
  count(database: string): string[];
  /** Read-only on the source: writes the archive of one database to stdout. */
  dump: string[];
  /** Drops the target database (guarded by the rails). */
  dropTarget: string[];
  /** Reads the archive from stdin and restores it into the target database. */
  restore: string[];
  /** Every container of the compose project with its state, one JSON object per line (parallel profile enabled). */
  states: string[];
}

const COUNT_SCRIPT = (database: string) =>
  `const d = db.getSiblingDB(${JSON.stringify(database)}); const out = {}; d.getCollectionNames().filter((n) => !n.startsWith('system.')).sort().forEach((n) => { out[n] = d.getCollection(n).countDocuments({}); }); print(JSON.stringify(out));`;

export function buildCommands(options: CopyOptions): CopyCommands {
  const base = composeBase(options);
  const exec = [...base, 'exec', '-T', MONGO_SERVICE];
  return {
    count: (database) => [
      ...exec,
      'mongosh',
      ...LOCAL,
      '--quiet',
      '--eval',
      COUNT_SCRIPT(database),
    ],
    dump: [...exec, 'mongodump', ...LOCAL, '--db', options.source, '--archive', '--quiet'],
    dropTarget: [
      ...exec,
      'mongosh',
      ...LOCAL,
      '--quiet',
      '--eval',
      `db.getSiblingDB(${JSON.stringify(options.target)}).dropDatabase()`,
    ],
    restore: [
      ...exec,
      'mongorestore',
      ...LOCAL,
      '--archive',
      '--quiet',
      '--nsInclude',
      `${options.source}.*`,
      '--nsFrom',
      `${options.source}.*`,
      '--nsTo',
      `${options.target}.*`,
    ],
    states: [...base, '--profile', 'parallel', 'ps', '--all', '--format', 'json'],
  };
}

export type Counts = Record<string, number>;

/** Differences between the document counts of source and target after the restore; empty means the copy is complete. */
export function compareCounts(source: Counts, target: Counts): string[] {
  const problems: string[] = [];
  for (const name of Object.keys(source).sort()) {
    if (!(name in target))
      problems.push(
        `collection ${name} is missing in the target (source has ${source[name]} documents)`,
      );
    else if (source[name] !== target[name])
      problems.push(`collection ${name}: source ${source[name]}, target ${target[name]}`);
  }
  for (const name of Object.keys(target).sort()) {
    if (!(name in source))
      problems.push(`collection ${name} exists only in the target (${target[name]} documents)`);
  }
  return problems;
}

/** Service name to container state from `docker compose ps --all --format json` (one JSON object per line, or one array). */
export function parseServiceStates(output: string): Record<string, string> {
  const text = output.trim();
  const rows: { Service?: string; State?: string }[] = text.startsWith('[')
    ? JSON.parse(text)
    : text
        .split(/\r?\n/)
        .filter((line) => line.trim().startsWith('{'))
        .map((line) => JSON.parse(line));
  const states: Record<string, string> = {};
  for (const row of rows)
    if (row.Service) states[row.Service] = (row.State ?? 'unknown').toLowerCase();
  return states;
}

/** A container in any state but exited or dead (running, restarting, paused, created) may hold or reopen the target database. */
export const isLive = (state: string | undefined): boolean =>
  state !== undefined && state !== 'exited' && state !== 'dead';

export interface CopyVerdict {
  /** 0: complete, or different only because the live source moved; 1: the copy is not what the source was. */
  code: 0 | 1;
  problems: string[];
  /** How the source moved while it was copied. */
  sourceMoved: string[];
}

/**
 * Judges the counts after a copy. A target that differs from the source as counted before is only excused when every difference lies
 * between the count before and the count after on the source itself (the household kept working while the dump ran).
 */
export function judgeCopy(before: Counts, after: Counts, sourceAfter: Counts): CopyVerdict {
  const problems = compareCounts(before, after);
  const sourceMoved = compareCounts(before, sourceAfter);
  if (problems.length === 0) return { code: 0, problems, sourceMoved };
  const names = new Set([...Object.keys(before), ...Object.keys(after)]);
  const excused = [...names].every((name) => {
    const b = before[name] ?? 0;
    const t = after[name] ?? 0;
    const a = sourceAfter[name] ?? 0;
    return t >= Math.min(b, a) && t <= Math.max(b, a);
  });
  return { code: excused ? 0 : 1, problems, sourceMoved };
}

export const USAGE = `Copy the live database into the database of the parallel run (plan section 10, step 2).

Usage: node scripts/parallel-copy-db.ts [options]

  mongodump of the source database, streamed into mongorestore as the target database, both inside the compose "mongo"
  service. The source is only read. The target database is dropped first and replaced completely.

Options
  --source <db>      database to read; default ${DEFAULT_SOURCE_DB}
  --target <db>      database to replace; default ${DEFAULT_TARGET_DB}. Any other name needs --force
  --force            allow a target other than ${DEFAULT_TARGET_DB} (never the same as the source, never ${DEFAULT_SOURCE_DB})
  --yes, -y          do not ask for confirmation
  --dry-run          print what would happen and the commands, run nothing
  --project, -p <n>  compose project name (default: the one of docker-compose.yml)
  --file, -f <file>  compose file (repeatable; default docker-compose.yml)
  --help, -h         this text

Prerequisites
  - Node.js 24 or newer on the Docker host (it runs this script; Docker runs the Mongo tools).
  - Run it from a checkout of the repository on the Docker host, with the "mongo" service running and healthy
    (docker compose up -d mongo). It needs only Docker; no mongodump or mongorestore on the host.
  - Take a backup first (docker compose run --rm backup once). The tool never writes to the source, but a backup is cheap.
  - Stop app-next while copying (docker compose --profile parallel stop app-next). The tool refuses to run while it is in any state but exited.
  - Start app-next only after the copy: on an empty database the .NET application seeds default rooms and profiles.
`;
