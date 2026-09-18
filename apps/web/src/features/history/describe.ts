import type { AuditEntry } from '@huishoudplanner/shared';
import { format, hasMessage, t, type MessageKey } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';

export const SYSTEM_ACTOR_ID = '000000000000000000000000';

/** Names known on the client, to turn ids in audit entries into readable text. */
export interface NameLookup {
  users: Map<string, string>;
  tasks: Map<string, string>;
  rooms: Map<string, string>;
  plans: Map<string, string>;
  intervals: Map<string, string>;
  /** Occurrence id → task name, collected from loaded create/delete entries. */
  occurrences: Map<string, string>;
  timezone: string;
}

type Json = Record<string, unknown>;

const isRecord = (value: unknown): value is Json =>
  typeof value === 'object' && value !== null && !Array.isArray(value);
const str = (value: unknown): string | undefined => (typeof value === 'string' && value ? value : undefined);

const DATE_FIELDS = new Set(['date', 'plannedDate', 'cycleAnchorDate']);
const DATETIME_FIELDS = new Set(['completedAt', 'lastCompletedAt', 'at']);
const USER_FIELDS = new Set(['defaultAssigneeId', 'assigneeId', 'completedBy']);

function isMinutesPath(path: string): boolean {
  return (
    path === 'durationMinutes' ||
    path === 'durationMinutesSnapshot' ||
    path.startsWith('dailyBudgetMinutes.') ||
    path.startsWith('maxDailyMinutes.')
  );
}

export function actorName(entry: AuditEntry, names: NameLookup): string {
  if (entry.actorId === SYSTEM_ACTOR_ID) return t('history.system');
  return names.users.get(entry.actorId) ?? t('tasks.unknownUser');
}

export function entityName(entry: AuditEntry, names: NameLookup): string {
  const { entityId: id, before, after } = entry;
  const unknown = t('history.unknownEntity');
  switch (entry.entity) {
    case 'task':
      return names.tasks.get(id) ?? str(after.name) ?? str(before.name) ?? unknown;
    case 'room':
      return names.rooms.get(id) ?? str(after.name) ?? str(before.name) ?? unknown;
    case 'user':
      return names.users.get(id) ?? str(after.name) ?? str(before.name) ?? unknown;
    case 'cyclePlan':
      return names.plans.get(id) ?? str(after.name) ?? str(before.name) ?? unknown;
    case 'occurrence':
      {
        const task = str(after.taskNameSnapshot) ?? str(before.taskNameSnapshot) ?? names.occurrences.get(id) ?? t('history.entity.occurrence');
        const room = str(after.roomNameSnapshot) ?? str(before.roomNameSnapshot);
        const date = str(after.date) ?? str(before.date) ?? str(entry.meta?.to) ?? str(entry.meta?.from);
        if (room && date) return format('history.occurrenceContext', { task, room, date: formatValue('date', date, names) });
        if (date) return format('history.occurrenceDateContext', { task, date: formatValue('date', date, names) });
        return task;
      }
    case 'settings':
      return t('history.settingsName');
    case 'cycle':
      return format('history.cycleName', { index: typeof after.index === 'number' ? after.index + 1 : '?' });
    case 'import':
      return t('history.entity.import');
  }
}

function fieldLabel(path: string): string {
  const key = `history.field.${path}`;
  return hasMessage(key) ? t(key) : path;
}

export function formatValue(path: string, value: unknown, names: NameLookup): string {
  const field = path.split('.').at(-1) ?? path;
  const none = t('history.value.none');
  if (value === null || value === undefined) return USER_FIELDS.has(field) ? t('tasks.anyone') : none;
  if (typeof value === 'boolean') return t(value ? 'history.value.yes' : 'history.value.no');
  if (typeof value === 'number') return String(value);

  if (typeof value === 'string') {
    if (DATE_FIELDS.has(field)) {
      // Day keys are already calendar dates; instants are shown in the app timezone.
      const date = /^\d{4}-\d{2}-\d{2}$/.test(value) ? new Date(`${value}T12:00:00Z`) : new Date(value);
      return new Intl.DateTimeFormat(getLocale(), {
        timeZone: /^\d{4}-\d{2}-\d{2}$/.test(value) ? 'UTC' : names.timezone,
        day: '2-digit',
        month: '2-digit',
        year: 'numeric',
      }).format(date);
    }
    if (DATETIME_FIELDS.has(field)) {
      return new Intl.DateTimeFormat(getLocale(), {
        timeZone: names.timezone,
        day: '2-digit',
        month: '2-digit',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      }).format(new Date(value));
    }
    if (USER_FIELDS.has(field)) return names.users.get(value) ?? t('tasks.unknownUser');
    if (field === 'roomId') return names.rooms.get(value) ?? value;
    if (field === 'intervalKey') return names.intervals.get(value) ?? value;
    if (field === 'planId') return names.plans.get(value) ?? value;
    if (field === 'status') {
      const key = `history.status.${value}`;
      return hasMessage(key) ? t(key) : value;
    }
    return value;
  }

  if (Array.isArray(value)) {
    if (value.length === 0) return none;
    if (field === 'unavailableWeekdays') {
      return value.map((d) => (hasMessage(`weekdayLong.${d}`) ? t(`weekdayLong.${d}` as MessageKey) : String(d))).join(', ');
    }
    if (field === 'vacationRanges') {
      return value.map((r) => (isRecord(r) ? `${String(r.from)} t/m ${String(r.to)}` : String(r))).join(', ');
    }
    if (field === 'weekThemes') return value.map((v) => str(v) ?? none).join(' | ');
    if (value.every((v) => typeof v === 'string' || typeof v === 'number')) return value.join(', ');
    return String(value.length);
  }

  return JSON.stringify(value);
}

/** Nested plain objects become dotted paths (arrays stay whole). */
function flatten(obj: Json, prefix = '', out = new Map<string, unknown>()): Map<string, unknown> {
  for (const [key, value] of Object.entries(obj)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (isRecord(value)) flatten(value, path, out);
    else out.set(path, value);
  }
  return out;
}

function updateLines(entry: AuditEntry, names: NameLookup, actor: string, entity: string): string[] {
  const lines: string[] = [];
  const before = flatten(entry.before);
  const after = flatten(entry.after);

  if (before.has('slots') || after.has('slots')) {
    const count = (v: unknown) => (Array.isArray(v) ? v.length : 0);
    lines.push(
      format('history.action.slots', { actor, entity, added: count(after.get('slots')), removed: count(before.get('slots')) }),
    );
  }

  const paths = [...new Set([...before.keys(), ...after.keys()])].filter((p) => p !== 'slots');
  for (const path of paths) {
    const b = before.get(path);
    const a = after.get(path);
    const minutes = isMinutesPath(path) && typeof a === 'number';
    lines.push(
      format('history.action.update', {
        actor,
        field: fieldLabel(path),
        entity,
        before: formatValue(path, b, names),
        after: minutes ? `${a} min` : formatValue(path, a, names),
      }),
    );
  }
  return lines.length > 0 ? lines : [format('history.action.updateNoField', { actor, entity })];
}

/** One or more Dutch sentences describing an audit entry. */
export function describeEntry(entry: AuditEntry, names: NameLookup): string[] {
  const actor = actorName(entry, names);
  const entity = entityName(entry, names);
  const { before, after } = entry;
  const meta = entry.meta ?? {};

  switch (entry.action) {
    case 'create':
      return [format('history.action.create', { actor, type: t(`history.entity.${entry.entity}` as MessageKey), entity })];
    case 'update':
      return updateLines(entry, names, actor, entity);
    case 'delete':
      return [format('history.action.delete', { actor, entity })];
    case 'complete': {
      const doer = str(meta.completedBy);
      return [
        doer && doer !== entry.actorId
          ? format('history.action.completeFor', { actor, entity, doer: formatValue('completedBy', doer, names) })
          : format('history.action.complete', { actor, entity }),
      ];
    }
    case 'uncomplete':
      return [format('history.action.uncomplete', { actor, entity })];
    case 'skip': {
      const reason = str(after.skipReason);
      return [
        reason
          ? format('history.action.skipReason', { actor, entity, reason })
          : format('history.action.skip', { actor, entity }),
      ];
    }
    case 'reschedule':
      return [
        format('history.action.reschedule', {
          actor,
          entity,
          from: formatValue('date', before.date ?? meta.from, names),
          to: formatValue('date', after.date ?? meta.to, names),
        }),
      ];
    case 'assign':
      if (meta.claim) return [format('history.action.claim', { actor, entity })];
      if ('defaultAssigneeId' in after) {
        return [
          format('history.action.defaultAssign', {
            actor,
            entity,
            assignee: formatValue('defaultAssigneeId', after.defaultAssigneeId, names),
          }),
        ];
      }
      return [format('history.action.assign', { actor, entity, assignee: formatValue('assigneeId', after.assigneeId, names) })];
    case 'activate':
      return [format('history.action.activate', { actor, entity })];
    case 'ai-apply':
      return [format('history.action.aiApply', { actor, entity })];
    case 'reset':
      return [format('history.action.reset', { actor })];
  }
}

/** Occurrence ids → task names from entries that carry the snapshot (create/delete). */
export function collectOccurrenceNames(entries: AuditEntry[]): Map<string, string> {
  const names = new Map<string, string>();
  for (const entry of entries) {
    if (entry.entity !== 'occurrence') continue;
    const name = str(entry.after.taskNameSnapshot) ?? str(entry.before.taskNameSnapshot);
    if (name) names.set(entry.entityId, name);
  }
  return names;
}
