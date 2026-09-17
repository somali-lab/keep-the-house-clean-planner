import { DEFAULT_AI_PROMPTS, DEFAULT_INTERVALS, mondayOf, today } from '@huishoudplanner/shared';
import type { AuditContext } from '../audit/context.ts';
import type { SeedUser } from '../config.ts';
import { countRooms, createRoom } from '../data/rooms.ts';
import { insertSettingsIfMissing } from '../data/settings.ts';
import { countUsers, createUser } from '../data/users.ts';
import { ensureDefaultPlan } from './plans.ts';

export const SEED_BUDGET = { weekday: 60, weekend: 120 };

export const SEED_ROOMS: { name: string; virtual: boolean }[] = [
  { name: 'Keuken', virtual: false },
  { name: 'Badkamer', virtual: false },
  { name: 'Toilet', virtual: false },
  { name: 'Woonkamer', virtual: false },
  { name: 'Slaapkamer', virtual: false },
  { name: 'Hal', virtual: false },
  { name: 'Hele huis', virtual: true },
];

export interface SeedOptions {
  timezone: string;
  seedUsers: SeedUser[];
}

export interface SeedResult {
  settingsCreated: boolean;
  usersCreated: number;
  roomsCreated: number;
  planCreated: boolean;
}

/** Idempotent first-run data. Run at startup with a system context. */
export async function seed(ctx: AuditContext, options: SeedOptions): Promise<SeedResult> {
  const settingsCreated = await insertSettingsIfMissing(ctx, {
    cycleAnchorDate: mondayOf(today(options.timezone, ctx.clock.now())),
    weekStartsOn: 1,
    timezone: options.timezone,
    vacationRanges: [],
    intervals: DEFAULT_INTERVALS,
    aiProvider: { type: 'none' },
    aiPrompts: DEFAULT_AI_PROMPTS,
    promoteThreshold: 2,
    dismissedPromotions: [],
  });

  let usersCreated = 0;
  if ((await countUsers(ctx.db)) === 0) {
    for (const [index, user] of options.seedUsers.entries()) {
      await createUser(ctx, {
        name: user.name,
        color: user.color,
        unavailableWeekdays: [],
        dailyBudgetMinutes: SEED_BUDGET,
        role: index === 0 ? 'admin' : 'member',
      });
      usersCreated++;
    }
  }

  let roomsCreated = 0;
  if ((await countRooms(ctx.db)) === 0) {
    for (const [i, room] of SEED_ROOMS.entries()) {
      await createRoom(ctx, { name: room.name, virtual: room.virtual, sortOrder: (i + 1) * 10 });
      roomsCreated++;
    }
  }

  const planCreated = await ensureDefaultPlan(ctx);

  const result = { settingsCreated, usersCreated, roomsCreated, planCreated };
  if (settingsCreated || usersCreated > 0 || roomsCreated > 0 || planCreated) {
    ctx.log.info(result, 'seed completed');
  }
  return result;
}
