/**
 * Comparison logic of scripts/parallel-parity.ts: the live Node application (API v1) against `app-next` (API v2) on the same
 * copy of the data (docs/plans/dotnet-rewrite.md section 10, step 4). Everything in here is pure: the HTTP clients are passed in,
 * so the comparison is unit tested with fixtures (parity.test.ts) and the script only wires `fetch` to it.
 *
 * Known, intended differences are normalised away and listed here, in one place:
 *  - ids: v1 `_id` is v2 `id`;
 *  - instants: v1 prints `...Z`, v2 prints `...+00:00`; both are compared as the same instant;
 *  - an absent member equals `null` (v1 leaves out what is not set, v2 always prints it), and for the settings lists an absent list
 *    equals `[]`;
 *  - paging: v1 lists are plain arrays and v1 wraps badges and awards in an object; v2 lists are `{ items, nextCursor }` and are
 *    read page by page;
 *  - `version` (ADR-0022), the members that only v2 has (V2_ONLY_MEMBERS) and `startsInFuture` on the rows of the bonus schedule;
 *  - list order is only compared where the order is part of the contract (due list, users, rooms, cycles, balances).
 */

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

/** A client of one of the two applications. Reads only. */
export interface Reader {
  /** GET `path` (relative to the API root of that application) and return the parsed JSON body. */
  get(path: string): Promise<unknown>;
}

export type DifferenceKind = 'changed' | 'only-v1' | 'only-v2' | 'order' | 'duplicate' | 'error';

export interface Difference {
  kind: DifferenceKind;
  /** The record the difference belongs to (an id, a person, a task) or '' for the whole check. */
  key: string;
  /** Path inside the record, '' for the record itself. */
  path: string;
  v1?: unknown;
  v2?: unknown;
  message?: string;
}

export interface CheckResult {
  name: string;
  v1Count: number | null;
  v2Count: number | null;
  differences: Difference[];
  /** Extra context, e.g. counts per status. Informational. */
  note?: string;
}

/** Members that exist only in v2 (v1 has no equivalent), per resource, dropped before comparing. `version` is dropped everywhere. */
export const V2_ONLY_MEMBERS: Record<string, string[]> = {
  occurrences: ['periodOwnerId', 'cycleIndex', 'weekIndex'],
};

const INSTANT = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$/;

/** The same instant printed the same way, whatever offset notation the application used. Other strings are returned as they are. */
export function normalizeInstant(value: string): string {
  if (!INSTANT.test(value)) return value;
  const time = Date.parse(value);
  return Number.isNaN(time) ? value : new Date(time).toISOString();
}

/** Recursively: `_id` becomes `id`, instants are normalised, members that are `undefined` disappear. */
export function normalize(value: unknown): Json {
  if (typeof value === 'string') return normalizeInstant(value);
  if (Array.isArray(value)) return value.map(normalize);
  if (value !== null && typeof value === 'object') {
    const out: { [key: string]: Json } = {};
    for (const [key, member] of Object.entries(value)) {
      if (member === undefined) continue;
      out[key === '_id' ? 'id' : key] = normalize(member);
    }
    return out;
  }
  return (value ?? null) as Json;
}

/** Normalises a record and removes the members that are not compared (`version` and the given v2-only members). */
export function normalizeRecord(value: unknown, dropMembers: readonly string[] = []): Json {
  const normalized = normalize(value);
  if (normalized === null || typeof normalized !== 'object' || Array.isArray(normalized))
    return normalized;
  const drop = new Set(['version', ...dropMembers]);
  return Object.fromEntries(Object.entries(normalized).filter(([key]) => !drop.has(key)));
}

export interface PathDifference {
  path: string;
  v1: Json | undefined;
  v2: Json | undefined;
}

const isObject = (value: Json | undefined): value is { [key: string]: Json } =>
  value !== null && typeof value === 'object' && !Array.isArray(value);

/** Deep comparison of two normalised values. An absent member and `null` are equal; arrays are compared by position. */
export function deepDiff(v1: Json | undefined, v2: Json | undefined, path = ''): PathDifference[] {
  if ((v1 === undefined || v1 === null) && (v2 === undefined || v2 === null)) return [];
  if (Array.isArray(v1) && Array.isArray(v2)) {
    const out: PathDifference[] = [];
    for (let i = 0; i < Math.max(v1.length, v2.length); i++)
      out.push(...deepDiff(v1[i], v2[i], `${path}[${i}]`));
    return out;
  }
  if (isObject(v1) && isObject(v2)) {
    const out: PathDifference[] = [];
    for (const key of [...new Set([...Object.keys(v1), ...Object.keys(v2)])].sort()) {
      out.push(...deepDiff(v1[key], v2[key], path === '' ? key : `${path}.${key}`));
    }
    return out;
  }
  return v1 === v2 ? [] : [{ path, v1, v2 }];
}

export interface CompareListOptions {
  name: string;
  v1: readonly unknown[];
  v2: readonly unknown[];
  /** The identity of a record (applied to the normalised record). */
  keyOf: (record: { [key: string]: Json }) => string;
  /** The order of the list is part of the contract. */
  ordered?: boolean;
  dropMembers?: readonly string[];
  note?: string;
}

function index(
  records: readonly unknown[],
  keyOf: CompareListOptions['keyOf'],
  dropMembers: readonly string[],
  side: 'v1' | 'v2',
  differences: Difference[],
): { map: Map<string, Json>; order: string[] } {
  const map = new Map<string, Json>();
  const order: string[] = [];
  for (const raw of records) {
    const record = normalizeRecord(raw, dropMembers);
    if (!isObject(record)) {
      differences.push({
        kind: 'error',
        key: '',
        path: '',
        message: `${side}: a list entry is not an object`,
      });
      continue;
    }
    const key = keyOf(record);
    if (map.has(key))
      differences.push({
        kind: 'duplicate',
        key,
        path: '',
        message: `${side} lists "${key}" more than once`,
      });
    map.set(key, record);
    order.push(key);
  }
  return { map, order };
}

/** Compares two lists of records by identity: missing records, differing members, and (when ordered) the order. */
export function compareLists(options: CompareListOptions): CheckResult {
  const differences: Difference[] = [];
  const drop = options.dropMembers ?? [];
  const a = index(options.v1, options.keyOf, drop, 'v1', differences);
  const b = index(options.v2, options.keyOf, drop, 'v2', differences);
  for (const key of a.order) {
    const left = a.map.get(key)!;
    const right = b.map.get(key);
    if (right === undefined) {
      differences.push({ kind: 'only-v1', key, path: '', v1: left });
      continue;
    }
    for (const d of deepDiff(left, right))
      differences.push({ kind: 'changed', key, path: d.path, v1: d.v1, v2: d.v2 });
  }
  for (const key of b.order) {
    if (!a.map.has(key)) differences.push({ kind: 'only-v2', key, path: '', v2: b.map.get(key) });
  }
  if (options.ordered) {
    const common = (order: string[], other: Map<string, Json>) =>
      order.filter((key) => other.has(key));
    const left = common(a.order, b.map);
    const right = common(b.order, a.map);
    const at = left.findIndex((key, i) => key !== right[i]);
    if (at >= 0) {
      differences.push({
        kind: 'order',
        key: '',
        path: `position ${at + 1}`,
        v1: left.slice(Math.max(0, at - 1), at + 3),
        v2: right.slice(Math.max(0, at - 1), at + 3),
        message: 'the same records in a different order',
      });
    }
  }
  return {
    name: options.name,
    v1Count: options.v1.length,
    v2Count: options.v2.length,
    differences,
    ...(options.note ? { note: options.note } : {}),
  };
}

const idOf = (record: { [key: string]: Json }): string => String(record.id ?? '');

/** v1 wraps some lists in an object (`{ badges: [...] }`, `{ awards: [...] }`); a plain array is returned as it is. */
export function unwrapV1(body: unknown, member: string): unknown[] {
  if (Array.isArray(body)) return body;
  if (
    body !== null &&
    typeof body === 'object' &&
    Array.isArray((body as Record<string, unknown>)[member])
  ) {
    return (body as Record<string, unknown[]>)[member]!;
  }
  throw new Error(`expected a list or an object with "${member}"`);
}

/** A v2 list page. */
export interface V2Page {
  items: unknown[];
  nextCursor: string | null;
}

export function isV2Page(body: unknown): body is V2Page {
  return body !== null && typeof body === 'object' && Array.isArray((body as V2Page).items);
}

/** Reads every page of a v2 list. `path` may already carry a query string. */
export async function readAllV2(
  reader: Reader,
  path: string,
  pageSize = 100,
): Promise<{ items: unknown[]; first: V2Page }> {
  const items: unknown[] = [];
  let first: V2Page | undefined;
  let cursor: string | null = null;
  const separator = path.includes('?') ? '&' : '?';
  for (let pages = 0; pages < 10_000; pages++) {
    const url: string = `${path}${separator}limit=${pageSize}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`;
    const body = await reader.get(url);
    if (!isV2Page(body)) throw new Error(`${path}: expected { items, nextCursor }`);
    first ??= body;
    items.push(...body.items);
    if (!body.nextCursor) return { items, first };
    cursor = body.nextCursor;
  }
  throw new Error(`${path}: more than 10000 pages`);
}

export interface ParityRange {
  from: string;
  to: string;
}

export interface ParityOptions {
  range: ParityRange;
  /** Which checks to run; default all. */
  only?: readonly string[];
}

/** The names of the checks, in the order they run. */
export const CHECKS = [
  'health',
  'users',
  'rooms',
  'tasks',
  'plans',
  'cycles',
  'settings',
  'due',
  'occurrences',
  'balances',
  'badges',
  'awards',
] as const;
export type CheckName = (typeof CHECKS)[number];

const asRecord = (value: unknown): Record<string, unknown> =>
  value !== null && typeof value === 'object' ? (value as Record<string, unknown>) : {};

/** Settings members both applications have (v2 also has `bonusesInForce` and `id` details; v1 has no `version`). */
export const SHARED_SETTINGS_MEMBERS = [
  'cycleAnchorDate',
  'weekStartsOn',
  'timezone',
  'vacationRanges',
  'intervals',
  'aiProvider',
  'aiPrompts',
  'aiPromptTemplates',
  'completionControl',
  'promoteThreshold',
  'dismissedPromotions',
  'bonusSchedule',
  'bonusFloor',
  'currencyCode',
  'centsPerPoint',
  'rewardGoals',
] as const;

/** The settings lists that v1 leaves out when empty. */
const SETTINGS_LISTS = ['vacationRanges', 'dismissedPromotions', 'bonusSchedule'];

export function compareSettings(v1: unknown, v2: unknown): CheckResult {
  const left = asRecord(normalize(v1));
  const right = asRecord(normalize(v2));
  const pick = (record: Record<string, unknown>) => {
    const out: { [key: string]: Json } = {};
    for (const member of SHARED_SETTINGS_MEMBERS) {
      const value = record[member] as Json | undefined;
      out[member] =
        SETTINGS_LISTS.includes(member) && (value === undefined || value === null)
          ? []
          : (value ?? null);
      if (member === 'bonusSchedule' && Array.isArray(out[member])) {
        // startsInFuture is derived by v2 for the settings screen; v1 does not print it.
        out[member] = (out[member] as Json[]).map((row) =>
          isObject(row)
            ? Object.fromEntries(Object.entries(row).filter(([key]) => key !== 'startsInFuture'))
            : row,
        );
      }
    }
    return out;
  };
  const differences = deepDiff(pick(left), pick(right)).map((d): Difference => ({
    kind: 'changed',
    key: 'settings',
    path: d.path,
    v1: d.v1,
    v2: d.v2,
  }));
  return { name: 'settings', v1Count: 1, v2Count: 1, differences };
}

export function compareHealth(v1: unknown, v2: unknown): CheckResult {
  const a = asRecord(v1);
  const b = asRecord(v2);
  const differences: Difference[] = [];
  if (a.status !== 'ok' || a.mongo !== 'ok')
    differences.push({
      kind: 'error',
      key: 'v1',
      path: '',
      v1: a,
      message: 'the Node application is not healthy',
    });
  if (b.status !== 'ok' || b.database !== 'ok')
    differences.push({
      kind: 'error',
      key: 'v2',
      path: '',
      v2: b,
      message: 'app-next is not healthy',
    });
  return {
    name: 'health',
    v1Count: null,
    v2Count: null,
    differences,
    ...(typeof b.version === 'string' ? { note: `app-next version ${b.version}` } : {}),
  };
}

/** The ranked due list (order matters) and the counts of the v2 summary against the states of the v1 list. */
export function compareDue(v1: unknown[], v2: unknown[], v2Summary: unknown): CheckResult {
  const result = compareLists({
    name: 'due',
    v1,
    v2,
    keyOf: (r) => String(r.taskId ?? ''),
    ordered: true,
  });
  const states = (items: unknown[]) => items.map((item) => asRecord(item).state);
  const expected = {
    due: states(v1).filter((s) => s === 'due').length,
    overdue: states(v1).filter((s) => s === 'overdue').length,
  };
  const summary = asRecord(v2Summary);
  for (const member of ['due', 'overdue'] as const) {
    if (summary[member] !== expected[member]) {
      result.differences.push({
        kind: 'changed',
        key: 'summary',
        path: member,
        v1: expected[member],
        v2: summary[member] ?? null,
        message: 'v1 count of that state against the v2 summary',
      });
    }
  }
  result.note = `${expected.due} due, ${expected.overdue} overdue`;
  return result;
}

const countBy = (items: readonly unknown[], member: string): string =>
  Object.entries(
    items.reduce<Record<string, number>>((acc, item) => {
      const value = String(asRecord(item)[member] ?? 'none');
      acc[value] = (acc[value] ?? 0) + 1;
      return acc;
    }, {}),
  )
    .sort(([x], [y]) => x.localeCompare(y))
    .map(([value, n]) => `${value} ${n}`)
    .join(', ');

export function compareOccurrences(v1: unknown[], v2: unknown[], range: ParityRange): CheckResult {
  return compareLists({
    name: `occurrences ${range.from}..${range.to}`,
    v1,
    v2,
    keyOf: idOf,
    dropMembers: V2_ONLY_MEMBERS.occurrences!,
    note: `by status: ${countBy(v1, 'status') || 'none'}`,
  });
}

/** Balances per person, in the order of the user list, plus the currency settings. */
export function compareBalances(v1: unknown, v2: unknown, name = 'balances'): CheckResult {
  const a = asRecord(v1);
  const b = asRecord(v2);
  const result = compareLists({
    name,
    v1: Array.isArray(a.balances) ? a.balances : [],
    v2: Array.isArray(b.balances) ? b.balances : [],
    keyOf: (r) => String(r.personId ?? ''),
    ordered: true,
  });
  for (const member of ['from', 'to', 'currencyCode', 'centsPerPoint']) {
    for (const d of deepDiff(normalize(a[member]), normalize(b[member]))) {
      result.differences.push({ kind: 'changed', key: 'header', path: member, v1: d.v1, v2: d.v2 });
    }
  }
  return result;
}

/** Awards compared as (person, badge) pairs: the ids and the moment of an award are derived data and not part of the question. */
export function compareAwards(v1: unknown[], v2: unknown[]): CheckResult {
  const pair = (item: unknown) => {
    const r = asRecord(item);
    return { personId: String(r.personId ?? ''), badgeId: String(r.badgeId ?? '') };
  };
  const keyed = (items: unknown[]) =>
    items.map((item) => ({ ...pair(item), id: `${pair(item).personId}/${pair(item).badgeId}` }));
  const perPerson = (items: unknown[]) => countBy(items.map(pair), 'personId');
  const result = compareLists({
    name: 'awards',
    v1: keyed(v1),
    v2: keyed(v2),
    keyOf: idOf,
    note: `per person: ${perPerson(v1) || 'none'}`,
  });
  return result;
}

export interface ParityReport {
  results: CheckResult[];
  /** True when every check ran and found nothing. */
  ok: boolean;
}

function failed(name: string, error: unknown): CheckResult {
  return {
    name,
    v1Count: null,
    v2Count: null,
    differences: [
      {
        kind: 'error',
        key: '',
        path: '',
        message: error instanceof Error ? error.message : String(error),
      },
    ],
  };
}

/**
 * Runs the checks against the two applications. `v1` reads `/api/...` and `v2` reads `/api/v2/...` (the readers add the prefix).
 * A check that cannot read one of the sides is reported as an error difference; the others still run.
 */
export async function runParity(
  v1: Reader,
  v2: Reader,
  options: ParityOptions,
): Promise<ParityReport> {
  const wanted = (name: CheckName) => !options.only || options.only.includes(name);
  const results: CheckResult[] = [];
  const run = async (name: CheckName, check: () => Promise<CheckResult>) => {
    if (!wanted(name)) return;
    try {
      results.push(await check());
    } catch (error) {
      results.push(failed(name, error));
    }
  };
  const list = async (path: string, member?: string) => {
    const a = member ? unwrapV1(await v1.get(path), member) : unwrapV1(await v1.get(path), '');
    return a;
  };
  const range = `from=${options.range.from}&to=${options.range.to}`;

  await run('health', async () => compareHealth(await v1.get('/health'), await v2.get('/health')));
  for (const [name, path, ordered] of [
    ['users', '/users', true],
    ['rooms', '/rooms', true],
    ['tasks', '/tasks', false],
    ['plans', '/cycle-plans', false],
    ['cycles', '/cycles', true],
  ] as const) {
    await run(name, async () => {
      const [a, b] = await Promise.all([list(path), readAllV2(v2, path)]);
      return compareLists({ name, v1: a, v2: b.items, keyOf: idOf, ordered });
    });
  }
  await run('settings', async () =>
    compareSettings(await v1.get('/settings'), await v2.get('/settings')),
  );
  await run('due', async () => {
    const [a, b] = await Promise.all([list('/due'), readAllV2(v2, '/due')]);
    return compareDue(a, b.items, (b.first as unknown as Record<string, unknown>).summary);
  });
  await run('occurrences', async () => {
    const [a, b] = await Promise.all([
      list(`/occurrences?${range}`),
      readAllV2(v2, `/occurrences?${range}`),
    ]);
    return compareOccurrences(a, b.items, options.range);
  });
  await run('balances', async () => {
    const [a, b, c, d] = await Promise.all([
      v1.get('/points/balances'),
      v2.get('/points/balances'),
      v1.get(`/points/balances?${range}`),
      v2.get(`/points/balances?${range}`),
    ]);
    const all = compareBalances(a, b, 'balances (all time)');
    const ranged = compareBalances(c, d, `balances ${options.range.from}..${options.range.to}`);
    return {
      ...all,
      name: 'balances',
      differences: [
        ...all.differences,
        ...ranged.differences.map((x) => ({
          ...x,
          key: `${options.range.from}..${options.range.to} ${x.key}`.trim(),
        })),
      ],
    };
  });
  await run('badges', async () => {
    const [a, b] = await Promise.all([list('/badges', 'badges'), readAllV2(v2, '/badges')]);
    return compareLists({ name: 'badges', v1: a, v2: b.items, keyOf: idOf });
  });
  await run('awards', async () => {
    const [a, b] = await Promise.all([
      list('/badges/awards', 'awards'),
      readAllV2(v2, '/badges/awards'),
    ]);
    return compareAwards(a, b.items);
  });

  return { results, ok: results.every((r) => r.differences.length === 0) };
}

const show = (value: unknown): string => {
  if (value === undefined) return '(absent)';
  const text = JSON.stringify(value);
  return text.length > 120 ? `${text.slice(0, 117)}...` : text;
};

function describeDifference(d: Difference): string {
  const where = [d.key && `[${d.key}]`, d.path].filter(Boolean).join(' ');
  switch (d.kind) {
    case 'only-v1':
      return `${where || 'record'}: only in v1 (Node)`;
    case 'only-v2':
      return `${where || 'record'}: only in v2 (app-next)`;
    case 'order':
      return `${where}: ${d.message}; v1 ${show(d.v1)} v2 ${show(d.v2)}`;
    case 'changed':
      return `${where}: v1 ${show(d.v1)}  v2 ${show(d.v2)}${d.message ? `  (${d.message})` : ''}`;
    default:
      return `${where ? `${where}: ` : ''}${d.message ?? d.kind}`;
  }
}

/** The human-readable report. At most `maxDifferences` lines per check; the count of the rest is stated. */
export function formatReport(report: ParityReport, maxDifferences = 20): string {
  const lines: string[] = [];
  for (const r of report.results) {
    const counts =
      r.v1Count === null && r.v2Count === null
        ? ''
        : ` (v1 ${r.v1Count ?? '?'}, v2 ${r.v2Count ?? '?'})`;
    const status = r.differences.length === 0 ? 'OK  ' : 'DIFF';
    lines.push(`${status} ${r.name}${counts}${r.note ? `  ${r.note}` : ''}`);
    r.differences
      .slice(0, maxDifferences)
      .forEach((d) => lines.push(`       - ${describeDifference(d)}`));
    if (r.differences.length > maxDifferences)
      lines.push(`       ... and ${r.differences.length - maxDifferences} more`);
  }
  const bad = report.results.filter((r) => r.differences.length > 0);
  lines.push(
    '',
    report.ok
      ? `Parity: all ${report.results.length} checks equal.`
      : `Parity: ${bad.length} of ${report.results.length} checks differ (${bad.map((r) => r.name).join(', ')}).`,
  );
  return lines.join('\n');
}
