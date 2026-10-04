import { daysBetween } from '@/lib/dayKey';
import type { components } from '../../api/v2/schema';

/** The kinds of entity the history can be filtered on (`entity` of `GET /api/v2/audit`). */
export type AuditEntity =
  | 'task'
  | 'cyclePlan'
  | 'occurrence'
  | 'user'
  | 'room'
  | 'settings'
  | 'cycle'
  | 'points'
  | 'badge'
  | 'badgeAward'
  | 'import';

type Json = Record<string, unknown>;

/**
 * One entry of the history as `GET /api/v2/audit` answers it. `entity` and `action` stay plain text: a server that is newer than the
 * web app may add values, and the history still has to show such an entry. `before` and `after` hold the changed fields only.
 */
export interface AuditEntry {
  id: string;
  /** ISO instant. */
  at: string;
  actorId: string;
  entity: string;
  entityId: string;
  action: string;
  before: Json;
  after: Json;
  source: string;
  meta: Json | null;
}

export const toAuditEntry = (entry: components['schemas']['AuditEntryResponse']): AuditEntry => ({
  id: entry.id,
  at: entry.at,
  actorId: entry.actorId,
  entity: entry.entity,
  entityId: entry.entityId,
  action: entry.action,
  before: entry.before as Json,
  after: entry.after as Json,
  source: entry.source,
  meta: (entry.meta as Json | null | undefined) ?? null,
});

export interface AuditFilters {
  entity?: AuditEntity | '';
  entityId?: string;
  actorId?: string;
  /** ISO instants with a time zone, inclusive. */
  from?: string;
  to?: string;
}

/** The most one page of the history holds in the history view (the server allows 1 to 200). */
export const AUDIT_PAGE_SIZE = 50;

/** The query of one page: empty filters are left out, because the server reads a present value as a filter. */
export function auditQuery(filters: AuditFilters, cursor: string | null) {
  return {
    ...(filters.entity ? { entity: filters.entity } : {}),
    ...(filters.entityId ? { entityId: filters.entityId } : {}),
    ...(filters.actorId ? { actorId: filters.actorId } : {}),
    ...(filters.from ? { from: filters.from } : {}),
    ...(filters.to ? { to: filters.to } : {}),
    limit: String(AUDIT_PAGE_SIZE),
    ...(cursor ? { cursor } : {}),
  };
}

const WEEK_BONUS_KINDS = new Set(['bonus_week_done', 'bonus_week_ontime']);

/**
 * The first days of the weeks that the week bonuses of the loaded reconciliations name (key `<kind>:<personId>:<periodStart>`),
 * each once and in order. The ISO week number of such a day comes from the calendar of the server.
 */
export function bonusPeriodStarts(entries: readonly AuditEntry[]): string[] {
  const days = new Set<string>();
  for (const entry of entries) {
    const changes = entry.entity === 'points' && Array.isArray(entry.meta?.bonusChanges) ? entry.meta.bonusChanges : [];
    for (const change of changes) {
      const key = typeof change === 'object' && change !== null && 'key' in change && typeof change.key === 'string' ? change.key : '';
      const [kind, , periodStart] = key.split(':');
      if (kind && periodStart && WEEK_BONUS_KINDS.has(kind) && /^\d{4}-\d{2}-\d{2}$/.test(periodStart)) days.add(periodStart);
    }
  }
  return [...days].sort();
}

export interface DayRange {
  from: string;
  to: string;
}

/** The fewest calendar ranges, each at most `maxRangeDays` long (both ends included), that hold every one of the days. */
export function calendarRanges(days: readonly string[], maxRangeDays: number): DayRange[] {
  const ranges: DayRange[] = [];
  for (const day of [...new Set(days)].sort()) {
    const last = ranges.at(-1);
    if (last && daysBetween(last.from, day) + 1 <= maxRangeDays) last.to = day;
    else ranges.push({ from: day, to: day });
  }
  return ranges;
}

