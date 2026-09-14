import { DEFAULT_AI_PROMPTS, DEFAULT_INTERVALS } from '@huishoudplanner/shared';
import { afterEach, describe, expect, it } from 'vitest';
import { SYSTEM_ACTOR_ID } from '../src/audit/context.ts';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import { listRooms } from '../src/data/rooms.ts';
import { getSettings } from '../src/data/settings.ts';
import { listUsers } from '../src/data/users.ts';
import { SEED_ROOMS, seed } from '../src/domain/seed.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp | undefined;

afterEach(async () => {
  await t?.close();
  t = undefined;
});

function seedOptions(app: TestApp) {
  return { timezone: app.config.timezone, seedUsers: app.config.seedUsers };
}

describe('seed', () => {
  it('creates settings, default users, rooms and an empty active plan on first boot, audited as system', async () => {
    t = await createTestApp({ seed: false });
    const result = await seed(t.systemCtx(), seedOptions(t));
    expect(result).toEqual({ settingsCreated: true, usersCreated: 2, roomsCreated: 7, planCreated: true });

    const settings = await getSettings(t.db);
    expect(settings).toMatchObject({
      cycleAnchorDate: '2026-09-14', // Monday of the week containing Wed 2026-09-16
      weekStartsOn: 1,
      timezone: 'Europe/Amsterdam',
      vacationRanges: [],
      intervals: DEFAULT_INTERVALS,
      aiProvider: { type: 'none' },
      aiPrompts: DEFAULT_AI_PROMPTS,
      promoteThreshold: 2,
      dismissedPromotions: [],
    });

    const users = await listUsers(t.db);
    expect(users.map((u) => [u.name, u.color, u.dailyBudgetMinutes])).toEqual([
      ['Persoon 1', '#2563eb', { weekday: 60, weekend: 120 }],
      ['Persoon 2', '#db2777', { weekday: 60, weekend: 120 }],
    ]);

    const rooms = await listRooms(t.db);
    expect(rooms.map((r) => r.name)).toEqual(SEED_ROOMS.map((r) => r.name));
    expect(rooms.filter((r) => r.virtual).map((r) => r.name)).toEqual(['Hele huis']);

    expect(await findActivePlan(t.db)).toMatchObject({ name: 'Standaard', active: true, slots: [], draft: false });

    const audit = await t.db.collection(COLLECTIONS.auditLog).find({}).toArray();
    expect(audit).toHaveLength(1 + 2 + 7 + 1);
    expect(audit.every((e) => e.source === 'system' && SYSTEM_ACTOR_ID.equals(e.actorId))).toBe(true);
    expect(audit.filter((e) => e.entity === 'settings' && e.action === 'create')).toHaveLength(1);
    expect(audit.filter((e) => e.entity === 'cyclePlan' && e.action === 'create')).toHaveLength(1);
  });

  it('is idempotent across boots: no duplicates and no extra audit entries', async () => {
    t = await createTestApp({ seed: false });
    await seed(t.systemCtx(), seedOptions(t));
    const counts = async () => ({
      settings: await t!.db.collection(COLLECTIONS.settings).countDocuments(),
      users: await t!.db.collection(COLLECTIONS.users).countDocuments(),
      rooms: await t!.db.collection(COLLECTIONS.rooms).countDocuments(),
      plans: await t!.db.collection(COLLECTIONS.cyclePlans).countDocuments(),
      audit: await t!.db.collection(COLLECTIONS.auditLog).countDocuments(),
    });
    const first = await counts();

    t.clock.advance(7 * 24 * 3600 * 1000);
    const second = await seed(t.systemCtx(), seedOptions(t));
    expect(second).toEqual({ settingsCreated: false, usersCreated: 0, roomsCreated: 0, planCreated: false });
    expect(await counts()).toEqual(first);
    // anchor is not moved by a later boot
    expect((await getSettings(t.db))?.cycleAnchorDate).toBe('2026-09-14');
  });

  it('seeds a configurable number of users from SEED_USERS', async () => {
    t = await createTestApp({
      seed: false,
      env: {
        SEED_USERS: JSON.stringify([
          { name: 'Anna', color: '#111111' },
          { name: 'Bram', color: '#222222' },
          { name: 'Chris', color: '#333333' },
        ]),
      },
    });
    const result = await seed(t.systemCtx(), seedOptions(t));
    expect(result.usersCreated).toBe(3);
    expect((await listUsers(t.db)).map((u) => u.name)).toEqual(['Anna', 'Bram', 'Chris']);
  });

  it('uses the local week for the anchor (Sunday night UTC is already Monday in Amsterdam)', async () => {
    t = await createTestApp({ seed: false, now: '2026-09-20T22:30:00Z' });
    await seed(t.systemCtx(), seedOptions(t));
    expect((await getSettings(t.db))?.cycleAnchorDate).toBe('2026-09-21');
  });

  it('does not add users when some already exist', async () => {
    t = await createTestApp({ seed: false });
    await seed(t.systemCtx(), { timezone: 'Europe/Amsterdam', seedUsers: [{ name: 'Solo', color: '#000000' }] });
    const again = await seed(t.systemCtx(), seedOptions(t));
    expect(again.usersCreated).toBe(0);
    expect((await listUsers(t.db)).map((u) => u.name)).toEqual(['Solo']);
  });
});
