#!/usr/bin/env node
/**
 * Compares the live Node application (API v1) with app-next (API v2) over HTTP, on the same copy of the data, and reports the
 * differences (docs/PARALLEL-RUN.md, plan section 10 step 4). Read-only: every request is a GET. Exit code 0 when every check is
 * equal, 1 when something differs or a side could not be read, 2 for a usage error, 3 when the run itself failed.
 *
 * Usage: node scripts/parallel-parity.ts [--v1 http://127.0.0.1:3000] [--v2 http://127.0.0.1:3001] [--profile-id <id>]
 *        [--from YYYY-MM-DD --to YYYY-MM-DD] [--only users,due] [--max-diffs 20] [--timeout-ms 30000] [--json]
 * The comparison rules and the intended differences that are ignored are documented in scripts/lib/parity.ts.
 */
import { isEntry } from './lib/runMain.ts';
import { httpReader } from './lib/httpReader.ts';
import {
  CHECKS,
  formatReport,
  runParity,
  type CheckName,
  type ParityRange,
  type Reader,
} from './lib/parity.ts';

class UsageError extends Error {}

interface Args {
  v1: string;
  v2: string;
  profileId: string | undefined;
  from: string | undefined;
  to: string | undefined;
  only: CheckName[] | undefined;
  maxDiffs: number;
  timeoutMs: number;
  json: boolean;
  help: boolean;
}

const USAGE = `Usage: node scripts/parallel-parity.ts [options]

  --v1 <url>          the Node application, default http://127.0.0.1:\${APP_PORT:-3000}
  --v2 <url>          app-next, default http://127.0.0.1:\${APP_NEXT_PORT:-3001}
  --profile-id <id>   send this profile id as X-Profile-Id on every request (reads do not need one)
  --from, --to <day>  occurrence and points range (YYYY-MM-DD); default: first to last day of the cycles of v1
  --only <a,b>        run only these checks: ${CHECKS.join(', ')}
  --max-diffs <n>     differences printed per check, default 20
  --timeout-ms <n>    timeout per request, default 30000
  --json              print the report as JSON instead of text
  --help, -h          this text

Exit codes: 0 all equal, 1 differences (or a check could not read a side), 2 usage error, 3 the run itself failed
(for example v1 unreachable while reading the cycles). Needs Node.js 24 or newer.

Read-only (GET only). Run it right after the copy: the household keeps changing the live data, and every change since the copy is a
real difference between the two databases, not a bug.`;

const DAY = /^\d{4}-\d{2}-\d{2}$/;

function parse(argv: string[]): Args {
  const args: Args = {
    v1: `http://127.0.0.1:${process.env.APP_PORT ?? '3000'}`,
    v2: `http://127.0.0.1:${process.env.APP_NEXT_PORT ?? '3001'}`,
    profileId: undefined,
    from: undefined,
    to: undefined,
    only: undefined,
    maxDiffs: 20,
    timeoutMs: 30_000,
    json: false,
    help: false,
  };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]!;
    const eq = arg.startsWith('--') ? arg.indexOf('=') : -1;
    const flag = eq > 0 ? arg.slice(0, eq) : arg;
    const take = (): string => {
      if (eq > 0) return arg.slice(eq + 1);
      const next = argv[++i];
      if (next === undefined) throw new UsageError(`${flag} needs a value`);
      return next;
    };
    switch (flag) {
      case '--v1':
        args.v1 = take();
        break;
      case '--v2':
        args.v2 = take();
        break;
      case '--profile-id':
        args.profileId = take();
        break;
      case '--from':
        args.from = take();
        break;
      case '--to':
        args.to = take();
        break;
      case '--only': {
        const names = take()
          .split(',')
          .map((s) => s.trim())
          .filter(Boolean);
        const unknown = names.filter((n) => !(CHECKS as readonly string[]).includes(n));
        if (unknown.length > 0)
          throw new UsageError(
            `Unknown check: ${unknown.join(', ')} (known: ${CHECKS.join(', ')})`,
          );
        args.only = names as CheckName[];
        break;
      }
      case '--max-diffs':
        args.maxDiffs = Number(take());
        if (!Number.isInteger(args.maxDiffs) || args.maxDiffs < 1)
          throw new UsageError('--max-diffs must be a whole number of at least 1');
        break;
      case '--timeout-ms':
        args.timeoutMs = Number(take());
        if (!Number.isInteger(args.timeoutMs) || args.timeoutMs < 1)
          throw new UsageError('--timeout-ms must be a whole number of at least 1');
        break;
      case '--json':
        args.json = true;
        break;
      case '--help':
      case '-h':
        args.help = true;
        break;
      default:
        throw new UsageError(`Unknown argument: ${arg}`);
    }
  }
  for (const [flag, value] of [
    ['--from', args.from],
    ['--to', args.to],
  ] as const) {
    if (value !== undefined && !DAY.test(value))
      throw new UsageError(`${flag} must be a day as YYYY-MM-DD`);
  }
  args.v1 = args.v1.replace(/\/+$/, '');
  args.v2 = args.v2.replace(/\/+$/, '');
  if ((args.from === undefined) !== (args.to === undefined))
    throw new UsageError('--from and --to go together');
  return args;
}

async function defaultRange(v1: Reader): Promise<ParityRange> {
  const cycles = (await v1.get('/cycles')) as { startDate: string; endDate: string }[];
  if (!Array.isArray(cycles) || cycles.length === 0) {
    const today = new Date();
    const day = (offset: number) =>
      new Date(today.getTime() + offset * 86_400_000).toISOString().slice(0, 10);
    return { from: day(-60), to: day(60) };
  }
  return {
    from: cycles.map((c) => c.startDate).sort()[0]!,
    to: cycles
      .map((c) => c.endDate)
      .sort()
      .at(-1)!,
  };
}

async function main(): Promise<number> {
  const args = parse(process.argv.slice(2));
  if (args.help) {
    console.log(USAGE);
    return 0;
  }
  const reader = (root: string) =>
    httpReader(root, {
      timeoutMs: args.timeoutMs,
      ...(args.profileId ? { profileId: args.profileId } : {}),
    });
  const v1 = reader(`${args.v1}/api`);
  const v2 = reader(`${args.v2}/api/v2`);
  const range = args.from && args.to ? { from: args.from, to: args.to } : await defaultRange(v1);
  console.log(
    `Parity of v1 ${args.v1} and v2 ${args.v2}, range ${range.from}..${range.to} (GET only)\n`,
  );
  const report = await runParity(v1, v2, { range, ...(args.only ? { only: args.only } : {}) });
  console.log(args.json ? JSON.stringify(report, null, 2) : formatReport(report, args.maxDiffs));
  return report.ok ? 0 : 1;
}

if (isEntry(import.meta.url)) {
  main().then(
    (code) => process.exit(code),
    (error: unknown) => {
      const message = error instanceof Error ? error.message : String(error);
      if (error instanceof UsageError) {
        console.error(`${message}\n\n${USAGE}`);
        process.exit(2);
      }
      console.error(`Parity could not run: ${message}`);
      process.exit(3);
    },
  );
}
