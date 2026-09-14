import { describe, expect, it } from 'vitest';
import { ConfigError, DEFAULT_SEED_USERS, loadConfig } from '../src/config.ts';

const base = { MONGO_URL: 'mongodb://localhost:27017/x' };

describe('loadConfig', () => {
  it('applies defaults', () => {
    const config = loadConfig(base);
    expect(config).toMatchObject({
      port: 3000,
      timezone: 'Europe/Amsterdam',
      seedUsers: DEFAULT_SEED_USERS,
      backupRetentionDays: 14,
      auditRetentionDays: undefined,
      disableScheduler: false,
      notify: { type: 'none' },
    });
  });

  it('treats empty strings as unset', () => {
    const config = loadConfig({ ...base, SEED_USERS: '', AUDIT_RETENTION_DAYS: '', PORT: '' });
    expect(config.seedUsers).toEqual(DEFAULT_SEED_USERS);
    expect(config.auditRetentionDays).toBeUndefined();
    expect(config.port).toBe(3000);
  });

  it('parses SEED_USERS JSON', () => {
    const config = loadConfig({
      ...base,
      SEED_USERS: '[{"name":"A","color":"#000000"},{"name":"B","color":"#ffffff"},{"name":"C","color":"#123456"}]',
    });
    expect(config.seedUsers.map((u) => u.name)).toEqual(['A', 'B', 'C']);
  });

  it('fails hard on invalid config without echoing values', () => {
    expect(() => loadConfig({})).toThrow(ConfigError);
    expect(() => loadConfig({ ...base, SEED_USERS: 'not json' })).toThrow(/SEED_USERS/);
    expect(() => loadConfig({ ...base, PORT: 'abc' })).toThrow(/PORT/);
    expect(() => loadConfig({ ...base, NOTIFY_TYPE: 'ntfy' })).toThrow(/NOTIFY_URL/);
    try {
      loadConfig({ ...base, NOTIFY_URL: 'secret-token-value' });
    } catch (err) {
      expect((err as Error).message).not.toContain('secret-token-value');
    }
  });

  it('only allows APP_FAKE_NOW in test mode', () => {
    const now = '2026-09-16T08:00:00Z';
    expect(() => loadConfig({ ...base, APP_FAKE_NOW: now })).toThrow(/APP_FAKE_NOW/);
    expect(loadConfig({ ...base, NODE_ENV: 'test', APP_FAKE_NOW: now }).fakeNow).toBe(now);
  });
});
