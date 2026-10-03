import {
  addExampleBadgesInputSchema,
  badgeAwardsQuerySchema,
  badgeProgressQuerySchema,
  createBadgeInputSchema,
  updateBadgeInputSchema,
} from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { ObjectId } from 'mongodb';
import { findBadgeById, imageBytes } from '../data/badges.ts';
import {
  addExampleBadges,
  badgeProgressOf,
  createBadgeFromInput,
  deleteBadgeAndAwards,
  listBadgeAwardViews,
  listBadgeViews,
  toBadgeView,
  updateBadgeFromInput,
} from '../domain/badges.ts';
import { notFound, parseOrThrow } from '../http/errors.ts';
import { activeQuerySchema, parseIdParam } from '../http/params.ts';
import { auditContext, requireAdmin } from '../identity/index.ts';

/** Whether an If-None-Match header (a list of entity tags, weak ones and * included) names the bytes with this hash. */
export function matchesETag(header: string | undefined, hash: string): boolean {
  if (!header) return false;
  return header.split(',').some((part) => {
    const tag = part.trim();
    return tag === '*' || tag.replace(/^W\//, '') === `"${hash}"`;
  });
}

/** Reads need no profile, as everywhere else; every write is an administrator's (ADR-0014). */
export const badgeRoutes: FastifyPluginAsync = async (app) => {
  app.get('/badges', async (request) => {
    const query = parseOrThrow(activeQuerySchema, request.query);
    return { badges: await listBadgeViews(app.deps.db, query) };
  });

  app.post('/badges', { preHandler: requireAdmin }, async (request, reply) => {
    const input = parseOrThrow(createBadgeInputSchema, request.body);
    return reply.status(201).send(toBadgeView(await createBadgeFromInput(auditContext(request), input)));
  });

  /** Adds the example badges that do not exist yet; calling it again changes nothing. */
  app.post('/badges/examples', { preHandler: requireAdmin }, async (request) => {
    const input = parseOrThrow(addExampleBadgesInputSchema, request.body ?? {});
    return addExampleBadges(auditContext(request), input.language);
  });

  app.get('/badges/awards', async (request) => {
    const query = parseOrThrow(badgeAwardsQuerySchema, request.query);
    return { awards: await listBadgeAwardViews(app.deps.db, query.personId ? new ObjectId(query.personId) : undefined) };
  });

  app.get('/badges/progress', async (request) => {
    const query = parseOrThrow(badgeProgressQuerySchema, request.query);
    return badgeProgressOf(app.deps.db, new ObjectId(query.personId));
  });

  /**
   * The image of a badge. Its address carries the content hash (`?v=`), so it is cached for good and a
   * new picture has a new address; the bytes are only ever served as the image type they were checked to be.
   */
  app.get('/badges/:id/image', async (request, reply) => {
    const badge = await findBadgeById(app.deps.db, parseIdParam(request.params));
    if (!badge?.image) throw notFound('badge image');
    const etag = `"${badge.image.hash}"`;
    // Only an address that carries the hash of these bytes may be cached for good; any other address revalidates.
    const version = (request.query as { v?: unknown } | undefined)?.v;
    const versioned = typeof version === 'string' && version.length >= 12 && badge.image.hash.startsWith(version);
    reply
      .header('ETag', etag)
      .header('Cache-Control', versioned ? 'public, max-age=31536000, immutable' : 'no-cache')
      .header('X-Content-Type-Options', 'nosniff')
      .header('Content-Security-Policy', "default-src 'none'; sandbox");
    if (matchesETag(request.headers['if-none-match'], badge.image.hash)) return reply.status(304).send();
    return reply.type(badge.image.contentType).send(imageBytes(badge.image));
  });

  app.patch('/badges/:id', { preHandler: requireAdmin }, async (request) => {
    const id = parseIdParam(request.params);
    const input = parseOrThrow(updateBadgeInputSchema, request.body);
    const badge = await updateBadgeFromInput(auditContext(request), id, input);
    if (!badge) throw notFound('badge');
    return toBadgeView(badge);
  });

  app.delete('/badges/:id', { preHandler: requireAdmin }, async (request) => {
    const id = parseIdParam(request.params);
    if (!(await deleteBadgeAndAwards(auditContext(request), id))) throw notFound('badge');
    return { deleted: true };
  });
};
