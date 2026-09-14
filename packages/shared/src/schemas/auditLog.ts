import { z } from 'zod';
import { isoDateTimeSchema, objectIdSchema } from './common.ts';

export const auditEntitySchema = z.enum([
  'task',
  'cyclePlan',
  'occurrence',
  'user',
  'settings',
  'cycle',
  'room',
  'import',
]);
export type AuditEntity = z.infer<typeof auditEntitySchema>;

export const auditActionSchema = z.enum([
  'create',
  'update',
  'delete',
  'complete',
  'uncomplete',
  'skip',
  'reschedule',
  'assign',
  'activate',
  'ai-apply',
  'reset',
]);
export type AuditAction = z.infer<typeof auditActionSchema>;

export const auditSourceSchema = z.enum(['ui', 'api', 'ai', 'system']);
export type AuditSource = z.infer<typeof auditSourceSchema>;

export const auditEntrySchema = z.object({
  _id: objectIdSchema,
  at: isoDateTimeSchema,
  actorId: objectIdSchema,
  entity: auditEntitySchema,
  entityId: objectIdSchema,
  action: auditActionSchema,
  before: z.record(z.string(), z.unknown()),
  after: z.record(z.string(), z.unknown()),
  source: auditSourceSchema,
  meta: z.record(z.string(), z.unknown()).optional(),
});
export type AuditEntry = z.infer<typeof auditEntrySchema>;

export const listAuditQuerySchema = z.object({
  entity: auditEntitySchema.optional(),
  entityId: objectIdSchema.optional(),
  actorId: objectIdSchema.optional(),
  source: auditSourceSchema.optional(),
  from: isoDateTimeSchema.optional(),
  to: isoDateTimeSchema.optional(),
  cursor: z.string().optional(),
  limit: z.coerce.number().int().min(1).max(200).default(50),
});
export type ListAuditQuery = z.infer<typeof listAuditQuerySchema>;
