import { z } from 'zod';
import { APP_TIMEZONE, hexColorSchema } from '@huishoudplanner/shared';

const emptyToUndefined = (v: unknown) => (v === '' ? undefined : v);

const booleanString = z
  .preprocess(emptyToUndefined, z.enum(['true', 'false']).default('false'))
  .transform((v) => v === 'true');

const optionalInt = z.preprocess(emptyToUndefined, z.coerce.number().int().min(1).optional());

export const seedUserSchema = z.object({
  name: z.string().trim().min(1),
  color: hexColorSchema,
});
export type SeedUser = z.infer<typeof seedUserSchema>;

export const DEFAULT_SEED_USERS: SeedUser[] = [
  { name: 'Persoon 1', color: '#2563eb' },
  { name: 'Persoon 2', color: '#db2777' },
];

const seedUsersSchema = z.preprocess(
  (v) => {
    if (v === undefined || v === '') return DEFAULT_SEED_USERS;
    if (typeof v !== 'string') return v;
    try {
      return JSON.parse(v);
    } catch {
      return 'invalid-json';
    }
  },
  z.array(seedUserSchema).min(1),
);

const envSchema = z
  .object({
    NODE_ENV: z.preprocess(
      emptyToUndefined,
      z.enum(['development', 'production', 'test']).default('production'),
    ),
    PORT: z.preprocess(emptyToUndefined, z.coerce.number().int().min(1).max(65535).default(3000)),
    MONGO_URL: z.string().min(1),
    TZ_APP: z.preprocess(emptyToUndefined, z.string().default(APP_TIMEZONE)),
    SEED_USERS: seedUsersSchema,
    LOG_LEVEL: z.preprocess(
      emptyToUndefined,
      z.enum(['fatal', 'error', 'warn', 'info', 'debug', 'trace', 'silent']).default('info'),
    ),
    BACKUP_DIR: z.preprocess(emptyToUndefined, z.string().default('/backups')),
    BACKUP_RETENTION_DAYS: z.preprocess(emptyToUndefined, z.coerce.number().int().min(1).default(14)),
    AUDIT_RETENTION_DAYS: optionalInt,
    AI_API_KEY: z.preprocess(emptyToUndefined, z.string().optional()),
    NOTIFY_TYPE: z.preprocess(
      emptyToUndefined,
      z.enum(['none', 'ntfy', 'homeassistant']).default('none'),
    ),
    NOTIFY_URL: z.preprocess(emptyToUndefined, z.string().url().optional()),
    NOTIFY_TOKEN: z.preprocess(emptyToUndefined, z.string().optional()),
    DISABLE_SCHEDULER: booleanString,
    APP_FAKE_NOW: z.preprocess(emptyToUndefined, z.iso.datetime({ offset: true }).optional()),
    WEB_DIST_DIR: z.preprocess(emptyToUndefined, z.string().optional()),
  })
  .superRefine((env, ctx) => {
    if (env.APP_FAKE_NOW && env.NODE_ENV !== 'test') {
      ctx.addIssue({
        code: 'custom',
        path: ['APP_FAKE_NOW'],
        message: 'APP_FAKE_NOW is only allowed when NODE_ENV=test',
      });
    }
    if (env.NOTIFY_TYPE !== 'none' && !env.NOTIFY_URL) {
      ctx.addIssue({
        code: 'custom',
        path: ['NOTIFY_URL'],
        message: 'NOTIFY_URL is required when NOTIFY_TYPE is not none',
      });
    }
  });

export interface AppConfig {
  nodeEnv: 'development' | 'production' | 'test';
  port: number;
  mongoUrl: string;
  timezone: string;
  seedUsers: SeedUser[];
  logLevel: 'fatal' | 'error' | 'warn' | 'info' | 'debug' | 'trace' | 'silent';
  backupDir: string;
  backupRetentionDays: number;
  auditRetentionDays: number | undefined;
  aiApiKey: string | undefined;
  notify: {
    type: 'none' | 'ntfy' | 'homeassistant';
    url: string | undefined;
    token: string | undefined;
  };
  disableScheduler: boolean;
  fakeNow: string | undefined;
  webDistDir: string | undefined;
}

export class ConfigError extends Error {
  readonly issues: string[];
  constructor(issues: string[]) {
    super(`Invalid configuration:\n  ${issues.join('\n  ')}`);
    this.name = 'ConfigError';
    this.issues = issues;
  }
}

/** Parses environment variables; throws ConfigError on invalid configuration. */
export function loadConfig(env: NodeJS.ProcessEnv = process.env): AppConfig {
  const result = envSchema.safeParse(env);
  if (!result.success) {
    // Never echo values: they may contain secrets.
    throw new ConfigError(
      result.error.issues.map((i) => `${i.path.join('.') || '(root)'}: ${i.message}`),
    );
  }
  const e = result.data;
  return {
    nodeEnv: e.NODE_ENV,
    port: e.PORT,
    mongoUrl: e.MONGO_URL,
    timezone: e.TZ_APP,
    seedUsers: e.SEED_USERS,
    logLevel: e.LOG_LEVEL,
    backupDir: e.BACKUP_DIR,
    backupRetentionDays: e.BACKUP_RETENTION_DAYS,
    auditRetentionDays: e.AUDIT_RETENTION_DAYS,
    aiApiKey: e.AI_API_KEY,
    notify: { type: e.NOTIFY_TYPE, url: e.NOTIFY_URL, token: e.NOTIFY_TOKEN },
    disableScheduler: e.DISABLE_SCHEDULER,
    fakeNow: e.APP_FAKE_NOW,
    webDistDir: e.WEB_DIST_DIR,
  };
}
