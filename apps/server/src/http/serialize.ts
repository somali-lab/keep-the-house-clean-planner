import { Binary, ObjectId } from 'mongodb';

/** JSON shape of a stored document: ObjectIds become hex strings, Dates ISO strings. */
export type ApiShape<T> = T extends ObjectId
  ? string
  : T extends Date
    ? string
    : T extends (infer U)[]
      ? ApiShape<U>[]
      : T extends object
        ? { [K in keyof T]: ApiShape<T[K]> }
        : T;

export function toApi<T>(value: T): ApiShape<T> {
  return convert(value) as ApiShape<T>;
}

function convert(value: unknown): unknown {
  if (value instanceof ObjectId) return value.toHexString();
  if (value instanceof Date) return value.toISOString();
  // Bytes (a badge image) have no JSON form of their own: base64, like the extended JSON of an export.
  if (value instanceof Binary) return value.toString('base64');
  if (Array.isArray(value)) return value.map(convert);
  if (value !== null && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [key, v] of Object.entries(value)) {
      if (v !== undefined) out[key] = convert(v);
    }
    return out;
  }
  return value;
}
