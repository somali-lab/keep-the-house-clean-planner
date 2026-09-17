import {
  addDays,
  cycleIndexFor,
  fromDayKey,
  isoWeekLabel,
  mondayOfIsoWeek,
  toDayKey,
  weekdaySun0,
  weekIndexFor,
} from '@huishoudplanner/shared';
import type { Db, ObjectId } from 'mongodb';
import { findPlanById } from '../../data/cyclePlans.ts';
import { findCycleByIndex } from '../../data/cycles.ts';
import { findOccurrences } from '../../data/occurrences.ts';
import { listRooms } from '../../data/rooms.ts';
import { getSettings, type SettingsDoc } from '../../data/settings.ts';
import { listTasks } from '../../data/tasks.ts';
import { listUsers } from '../../data/users.ts';
import { HttpError } from '../../http/errors.ts';

export interface SheetLine {
  name: string;
  room: string | null;
  minutes: number;
}

export interface SheetColumn {
  /** null = "wie dan ook" */
  id: string | null;
  name: string;
}

export interface SheetDay {
  dayKey: string;
  /** 0=Sunday..6=Saturday */
  weekday: number;
  /** One list of lines per column, same order as `columns`. */
  cells: SheetLine[][];
}

export interface WeekSheet {
  isoWeek: string;
  /** 1..4 */
  weekNumberInCycle: number;
  theme: string;
  from: string;
  to: string;
  columns: SheetColumn[];
  days: SheetDay[];
}

async function requireSettings(db: Db): Promise<SettingsDoc> {
  const settings = await getSettings(db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  return settings;
}

export const ANYONE_COLUMN_NAME = 'Wie dan ook';

/**
 * Builds printable sheets from generated occurrences (not the template), so
 * rescheduled items appear on the day they actually sit. Status is ignored:
 * the sheet is a blank checklist. Weeks whose cycle was never generated → 409.
 */
export async function buildWeekSheets(db: Db, fromWeek: string, weeks: number): Promise<WeekSheet[]> {
  const settings = await requireSettings(db);
  const monday = mondayOfIsoWeek(fromWeek);
  if (!monday) {
    throw new HttpError(400, 'validation_error', 'Invalid ISO week', [{ field: 'fromWeek', message: 'invalid_iso_week' }]);
  }
  const anchor = settings.cycleAnchorDate;
  const tz = settings.timezone;
  const mondays = Array.from({ length: weeks }, (_, i) => addDays(monday, i * 7));

  const cycles = await Promise.all(mondays.map((m) => findCycleByIndex(db, cycleIndexFor(m, anchor))));
  const missing = mondays.filter((_, i) => !cycles[i]).map((m) => isoWeekLabel(m));
  if (missing.length > 0) {
    throw new HttpError(409, 'weeks_not_generated', 'Some weeks have not been generated yet', undefined, {
      weeks: missing,
    });
  }

  const [occurrences, tasks, rooms, users] = await Promise.all([
    findOccurrences(db, {
      date: { $gte: fromDayKey(monday, tz), $lt: fromDayKey(addDays(monday, weeks * 7), tz) },
    }),
    listTasks(db),
    listRooms(db),
    listUsers(db),
  ]);

  const roomName = new Map(rooms.map((r) => [r._id.toHexString(), r.name]));
  const taskRoom = new Map(tasks.map((t) => [t._id.toHexString(), roomName.get(t.roomId.toHexString()) ?? null]));

  // Columns: active users, plus anyone else who still has occurrences here, then "wie dan ook".
  const referenced = new Set(occurrences.flatMap((o) => (o.assigneeId ? [o.assigneeId.toHexString()] : [])));
  const columns: SheetColumn[] = users
    .filter((u) => u.active || referenced.has(u._id.toHexString()))
    .map((u) => ({ id: u._id.toHexString(), name: u.name }));
  const knownIds = new Set(columns.map((c) => c.id));
  for (const id of referenced) {
    if (!knownIds.has(id)) columns.push({ id, name: '?' });
  }
  columns.push({ id: null, name: ANYONE_COLUMN_NAME });
  const columnIndex = (assigneeId: ObjectId | null) =>
    assigneeId === null ? columns.length - 1 : columns.findIndex((c) => c.id === assigneeId.toHexString());

  const planThemes = new Map<string, string[]>();
  for (const cycle of cycles) {
    if (cycle?.planId && !planThemes.has(cycle.planId.toHexString())) {
      const plan = await findPlanById(db, cycle.planId);
      planThemes.set(cycle.planId.toHexString(), plan?.weekThemes ?? []);
    }
  }

  return mondays.map((weekMonday, i) => {
    const cycle = cycles[i]!;
    const weekIndex = weekIndexFor(weekMonday, anchor);
    const days: SheetDay[] = Array.from({ length: 7 }, (_, d) => {
      const dayKey = addDays(weekMonday, d);
      return { dayKey, weekday: weekdaySun0(dayKey), cells: columns.map(() => [] as SheetLine[]) };
    });
    for (const occ of occurrences) {
      const dayKey = toDayKey(occ.date, tz);
      const day = days.find((d) => d.dayKey === dayKey);
      if (!day) continue;
      day.cells[columnIndex(occ.assigneeId)]!.push({
        name: occ.taskNameSnapshot,
        room: occ.roomNameSnapshot ?? taskRoom.get(occ.taskId.toHexString()) ?? null,
        minutes: occ.durationMinutesSnapshot,
      });
    }
    for (const day of days) {
      for (const cell of day.cells) cell.sort((a, b) => a.name.localeCompare(b.name, 'nl'));
    }
    return {
      isoWeek: isoWeekLabel(weekMonday),
      weekNumberInCycle: weekIndex + 1,
      theme: (cycle.planId ? planThemes.get(cycle.planId.toHexString())?.[weekIndex] : '') ?? '',
      from: weekMonday,
      to: addDays(weekMonday, 6),
      columns,
      days,
    };
  });
}

/** A single-day sheet: the week containing the day, reduced to that day. */
export async function buildDaySheet(db: Db, dayKey: string): Promise<WeekSheet> {
  const [sheet] = await buildWeekSheets(db, isoWeekLabel(dayKey), 1);
  return { ...sheet!, days: sheet!.days.filter((d) => d.dayKey === dayKey), from: dayKey, to: dayKey };
}
