import { resolve } from 'node:path';
import fastifyStatic from '@fastify/static';
import type { AiProviderSettings } from '@huishoudplanner/shared';
import Fastify, { type FastifyInstance, type FastifyServerOptions } from 'fastify';
import type { Db } from 'mongodb';
import type { Clock } from './clock.ts';
import type { AppConfig } from './config.ts';
import { createAiProvider } from './domain/ai/factory.ts';
import type { AiProvider } from './domain/ai/provider.ts';
import { createNotifier, type Notifier } from './domain/notify/notifier.ts';
import { closeBrowser } from './domain/pdf/browser.ts';
import { HttpError } from './http/errors.ts';
import { identityPlugin } from './identity/index.ts';
import { aiRoutes } from './routes/ai.ts';
import { auditRoutes } from './routes/audit.ts';
import { cyclePlanRoutes } from './routes/cyclePlans.ts';
import { cycleRoutes } from './routes/cycles.ts';
import { dueRoutes } from './routes/due.ts';
import { exportRoutes } from './routes/export.ts';
import { healthRoutes } from './routes/health.ts';
import { jobRoutes } from './routes/jobs.ts';
import { occurrenceRoutes } from './routes/occurrences.ts';
import { promoteRoutes } from './routes/promote.ts';
import { roomRoutes } from './routes/rooms.ts';
import { settingsRoutes } from './routes/settings.ts';
import { statsRoutes } from './routes/stats.ts';
import { taskRoutes } from './routes/tasks.ts';
import { transferRoutes } from './routes/transfer.ts';
import { userRoutes } from './routes/users.ts';

export type AiProviderFactory = (settings: AiProviderSettings) => AiProvider;

export interface AppDeps {
  db: Db;
  config: AppConfig;
  clock: Clock;
  /** Builds the provider selected in settings; secrets come from config (env) only. */
  aiProviderFor: AiProviderFactory;
  /** Null when NOTIFY_TYPE=none. */
  notifier: Notifier | null;
}

export interface RouteInfo {
  method: string;
  url: string;
}

export interface BuildAppOptions extends Omit<AppDeps, 'aiProviderFor' | 'notifier'> {
  logger?: FastifyServerOptions['logger'];
  /** Tests inject a mock provider here. */
  aiProviderFactory?: AiProviderFactory;
  /** Extra registrations before the app is ready (tests only). */
  configure?: (app: FastifyInstance) => void | Promise<void>;
}

declare module 'fastify' {
  interface FastifyInstance {
    deps: AppDeps;
    /** Every registered route; used by the audit coverage test to find write routes. */
    routeList: RouteInfo[];
  }
}

export async function buildApp(options: BuildAppOptions): Promise<FastifyInstance> {
  const { db, config, clock, logger } = options;
  const app = Fastify({
    logger: logger ?? {
      level: config.logLevel,
      redact: ['req.headers.authorization', 'req.headers["x-api-key"]'],
    },
  });

  const aiProviderFor: AiProviderFactory =
    options.aiProviderFactory ?? ((settings) => createAiProvider(settings, { apiKey: config.aiApiKey }));
  app.decorate('deps', { db, config, clock, aiProviderFor, notifier: createNotifier(config.notify) });

  const routeList: RouteInfo[] = [];
  app.decorate('routeList', routeList);
  app.addHook('onRoute', (route) => {
    for (const method of [route.method].flat()) routeList.push({ method: String(method), url: route.url });
  });

  // The shared PDF browser lives as long as the app.
  app.addHook('onClose', async () => {
    await closeBrowser();
  });

  app.setErrorHandler((error, request, reply) => {
    if (error instanceof HttpError) {
      return reply.status(error.statusCode).send({
        code: error.code,
        message: error.message,
        ...(error.details === undefined ? {} : { details: error.details }),
        ...(error.extra ?? {}),
      });
    }
    const statusCode =
      typeof (error as { statusCode?: unknown }).statusCode === 'number'
        ? (error as { statusCode: number }).statusCode
        : 500;
    if (statusCode >= 500) {
      request.log.error({ err: error }, 'unhandled error');
      return reply.status(500).send({ code: 'internal_error' });
    }
    return reply.status(statusCode).send({
      code: (error as { code?: string }).code ?? 'bad_request',
      message: (error as Error).message,
    });
  });

  await app.register(identityPlugin);
  await app.register(healthRoutes, { prefix: '/api' });
  await app.register(userRoutes, { prefix: '/api' });
  await app.register(roomRoutes, { prefix: '/api' });
  await app.register(settingsRoutes, { prefix: '/api' });
  await app.register(taskRoutes, { prefix: '/api' });
  await app.register(cyclePlanRoutes, { prefix: '/api' });
  await app.register(cycleRoutes, { prefix: '/api' });
  await app.register(occurrenceRoutes, { prefix: '/api' });
  await app.register(dueRoutes, { prefix: '/api' });
  await app.register(promoteRoutes, { prefix: '/api' });
  await app.register(aiRoutes, { prefix: '/api' });
  await app.register(statsRoutes, { prefix: '/api' });
  await app.register(auditRoutes, { prefix: '/api' });
  await app.register(exportRoutes, { prefix: '/api' });
  await app.register(transferRoutes, { prefix: '/api' });
  await app.register(jobRoutes, { prefix: '/api' });
  if (options.configure) await options.configure(app);

  if (config.webDistDir) {
    await app.register(fastifyStatic, { root: resolve(config.webDistDir), wildcard: false });
  }

  app.setNotFoundHandler((request, reply) => {
    // SPA fallback: client-side routes get index.html; API and asset misses stay JSON 404.
    const isPageRequest =
      config.webDistDir &&
      request.method === 'GET' &&
      !request.url.startsWith('/api/') &&
      !/\.[a-z0-9]+(\?.*)?$/i.test(request.url);
    if (isPageRequest) return reply.sendFile('index.html');
    return reply.status(404).send({ code: 'not_found' });
  });

  return app;
}
