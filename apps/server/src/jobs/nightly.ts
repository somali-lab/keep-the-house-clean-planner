import { randomUUID } from 'node:crypto';
import type { FastifyInstance } from 'fastify';
import cron, { type ScheduledTask } from 'node-cron';
import { systemContext, type AuditContext } from '../audit/context.ts';
import { runAuditRetention } from '../domain/auditRetention.ts';
import { computeDueList, summarizeDue } from '../domain/due.ts';
import { generateUpcoming, type GenerationResult } from '../domain/generation.ts';
import { runMorningNotify } from '../domain/notify/morning.ts';
import { reconcilePoints } from '../domain/points.ts';

export interface NightlyResult {
  runId: string;
  removed: number;
  generated: GenerationResult[];
  due: { due: number; overdue: number };
}

/** Generates the current and next cycle, reconciles the points ledger, then logs the due-engine summary. Safe to run repeatedly. */
export async function runNightly(ctx: AuditContext): Promise<NightlyResult> {
  const runId = randomUUID();
  const { generated, removed } = await generateUpcoming(ctx, runId);
  // Repairs ledger drift (a crash between an occurrence write and its ledger write) within a day (ADR-0011).
  const points = await reconcilePoints(ctx, 'nightly');
  const { items } = await computeDueList(ctx.db, ctx.clock.now());
  const due = summarizeDue(items);
  ctx.log.info(
    {
      runId,
      removed,
      points: { created: points.created, updated: points.updated, removed: points.removed, corrections: points.corrections.length },
      generated: generated.map((g) => ({ cycleIndex: g.cycleIndex, inserted: g.inserted, skipped: g.skipped })),
      due,
    },
    'nightly run completed',
  );
  return { runId, removed, generated, due };
}

export interface SchedulerHandle {
  stop(): void;
}

/**
 * All jobs in the app timezone: generation 03:00, audit retention 03:45 (only
 * with AUDIT_RETENTION_DAYS) and the morning message 07:30 (only with
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
