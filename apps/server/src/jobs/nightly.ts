import { randomUUID } from 'node:crypto';
import type { FastifyInstance } from 'fastify';
import cron, { type ScheduledTask } from 'node-cron';
import { systemContext, type AuditContext } from '../audit/context.ts';
import { runAuditRetention } from '../domain/auditRetention.ts';
import { runBackup } from '../domain/backup.ts';
import { computeDueList, summarizeDue } from '../domain/due.ts';
import { generateUpcoming, type GenerationResult } from '../domain/generation.ts';
import { runMorningNotify } from '../domain/notify/morning.ts';

export interface NightlyResult {
  runId: string;
  generated: GenerationResult[];
  due: { due: number; overdue: number };
}

/** Generates the current and next cycle, then logs the due-engine summary. Safe to run repeatedly. */
export async function runNightly(ctx: AuditContext): Promise<NightlyResult> {
  const runId = randomUUID();
  const generated = await generateUpcoming(ctx, runId);
  const { items } = await computeDueList(ctx.db, ctx.clock.now());
  const due = summarizeDue(items);
  ctx.log.info(
    {
      runId,
      generated: generated.map((g) => ({ cycleIndex: g.cycleIndex, inserted: g.inserted, skipped: g.skipped })),
      due,
    },
    'nightly run completed',
  );
  return { runId, generated, due };
}

export interface SchedulerHandle {
  stop(): void;
}

/**
 * All jobs in the app timezone: generation 03:00, backup 03:30, audit retention
 * 03:45 (only with AUDIT_RETENTION_DAYS) and the morning message 07:30 (only with
 * a notifier). A failing job is logged and never stops the others.
 * Returns null when DISABLE_SCHEDULER=true.
 */
export function startScheduler(app: FastifyInstance): SchedulerHandle | null {
  const { config, db, clock, notifier } = app.deps;
  if (config.disableScheduler) return null;

  const job = (expression: string, name: string, run: () => Promise<unknown>): ScheduledTask =>
    cron.schedule(
      expression,
      async () => {
        try {
          await run();
        } catch (err) {
          app.log.error({ err, job: name }, 'scheduled job failed');
        }
      },
      { timezone: config.timezone, name, noOverlap: true },
    );

  const tasks: ScheduledTask[] = [
    job('0 3 * * *', 'nightly-generation', () => runNightly(systemContext({ db, clock }, app.log))),
    job('30 3 * * *', 'nightly-backup', () => runBackup({ config, clock, log: app.log })),
  ];
  if (config.auditRetentionDays !== undefined) {
    tasks.push(
      job('45 3 * * *', 'audit-retention', () =>
        runAuditRetention({ db, clock, log: app.log, retentionDays: config.auditRetentionDays }),
      ),
    );
  }
  if (notifier) {
    tasks.push(job('30 7 * * *', 'morning-notify', () => runMorningNotify({ db, clock, log: app.log, notifier })));
  }

  return {
    stop: () => {
      for (const task of tasks) void task.stop();
    },
  };
}
