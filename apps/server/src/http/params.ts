import { objectIdSchema } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import { z } from 'zod';
import { parseOrThrow } from './errors.ts';

const idParamsSchema = z.object({ id: objectIdSchema });

export function parseIdParam(params: unknown): ObjectId {
  return new ObjectId(parseOrThrow(idParamsSchema, params).id);
}

export const booleanQuery = z.enum(['true', 'false']).transform((v) => v === 'true');

export const activeQuerySchema = z.object({ active: booleanQuery.optional() });

export function toObjectId(hex: string): ObjectId;
export function toObjectId(hex: string | null): ObjectId | null;
export function toObjectId(hex: string | null): ObjectId | null {
  return hex === null ? null : new ObjectId(hex);
}
