type Doc = Record<string, unknown>;

export interface FieldDiff {
  before: Doc;
  after: Doc;
}

export interface DiffOptions {
  /** Top-level keys to leave out. Default: ['updatedAt']. */
  ignore?: string[];
}

function isBsonValue(value: unknown): value is { _bsontype: string; equals?: (o: unknown) => boolean } {
  return typeof value === 'object' && value !== null && '_bsontype' in value;
}

function isPlainObject(value: unknown): value is Doc {
  if (typeof value !== 'object' || value === null || isBsonValue(value)) return false;
  const proto = Object.getPrototypeOf(value);
  return proto === Object.prototype || proto === null;
}

export function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a instanceof Date || b instanceof Date) {
    return a instanceof Date && b instanceof Date && a.getTime() === b.getTime();
  }
  if (isBsonValue(a) || isBsonValue(b)) {
    if (!isBsonValue(a) || !isBsonValue(b) || a._bsontype !== b._bsontype) return false;
    return typeof a.equals === 'function' ? a.equals(b) : String(a) === String(b);
  }
  if (Array.isArray(a) || Array.isArray(b)) {
    if (!Array.isArray(a) || !Array.isArray(b) || a.length !== b.length) return false;
    return a.every((item, i) => deepEqual(item, b[i]));
  }
  if (isPlainObject(a) && isPlainObject(b)) {
    const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
    for (const key of keys) {
      if (!deepEqual(a[key], b[key])) return false;
    }
    return true;
  }
  return false;
}

/**
 * Changed fields only. Nested plain objects are diffed recursively; arrays,
 * dates and BSON values are compared as a whole. Absent keys stay absent.
 */
export function diffFields(
  before: Doc | null | undefined,
  after: Doc | null | undefined,
  options: DiffOptions = {},
): FieldDiff {
  const ignore = new Set(options.ignore ?? ['updatedAt']);
  const b = before ?? {};
  const a = after ?? {};
  const result: FieldDiff = { before: {}, after: {} };

  for (const key of new Set([...Object.keys(b), ...Object.keys(a)])) {
    if (ignore.has(key)) continue;
    const bv = b[key];
    const av = a[key];
    if (isPlainObject(bv) && isPlainObject(av)) {
      const nested = diffFields(bv, av, { ignore: [] });
      if (Object.keys(nested.before).length > 0 || Object.keys(nested.after).length > 0) {
        result.before[key] = nested.before;
        result.after[key] = nested.after;
      }
      continue;
    }
    if (deepEqual(bv, av)) continue;
    if (bv !== undefined) result.before[key] = bv;
    if (av !== undefined) result.after[key] = av;
  }
  return result;
}

export function isEmptyDiff(diff: FieldDiff): boolean {
  return Object.keys(diff.before).length === 0 && Object.keys(diff.after).length === 0;
}
