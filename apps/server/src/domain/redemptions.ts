import { DEFAULT_CURRENCY_CODE, fromDayKey, mondayOf, toDayKey, type UserRole } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import {
  deleteRedemption,
  findRedemptionById,
  findRedemptionByRequestId,
  insertRedemption,
  redemptionKey,
  sumPersonBalance,
  type RedemptionDoc,
} from '../data/points.ts';
import { getSettings } from '../data/settings.ts';
import { findUserById } from '../data/users.ts';
import { HttpError, notFound } from '../http/errors.ts';
import { exclusively } from './points.ts';

export interface BookRedemptionInput {
  personId: ObjectId;
  /** Points to redeem, a positive integer. */
  points: number;
  note: string | null;
  /** Client idempotency key; a repeat of the same request replays the stored booking. */
  requestId?: string;
}

export interface BookRedemptionResult {
  doc: RedemptionDoc;
  /** False when a repeated requestId replayed an existing booking. */
  created: boolean;
}

const idempotencyConflict = () =>
  new HttpError(409, 'idempotency_key_conflict', 'This request key was already used for a different request');

/** The same key for the same booking returns the stored one; anything else is a conflict. */
function replayOrConflict(existing: RedemptionDoc, input: BookRedemptionInput): BookRedemptionResult {
  const same = existing.personId.equals(input.personId) && existing.amount === -input.points && (existing.note ?? null) === input.note;
  if (!same) throw idempotencyConflict();
  return { doc: existing, created: false };
}

/**
 * Books a redemption (requirements 4.12): a ledger entry of kind `redemption` with a negative amount, dated
 * today, that keeps the factor in force. The booking is refused when it would make the balance of the
 * person negative. It runs inside the same per-database queue as the reconciliation, so within the one
 * process (ADR-0005) the balance check and the insert of two bookings never interleave. The caller has
 * already decided that the actor may book for this person.
 */
export function bookRedemption(ctx: AuditContext, input: BookRedemptionInput): Promise<BookRedemptionResult> {
  return exclusively(ctx.db, async () => {
    if (input.requestId) {
      const existing = await findRedemptionByRequestId(ctx.db, input.requestId);
      if (existing) return replayOrConflict(existing, input);
    }
    const settings = await getSettings(ctx.db);
    if (!settings) throw new HttpError(500, 'settings_missing');
    const person = await findUserById(ctx.db, input.personId);
    if (!person?.active) {
      throw new HttpError(400, 'validation_error', 'Invalid person', [
        { field: 'personId', message: person ? 'inactive_user' : 'unknown_user' },
      ]);
    }
    const balance = await sumPersonBalance(ctx.db, input.personId);
    if (input.points > balance) {
      throw new HttpError(409, 'insufficient_balance', 'The balance is too low for this redemption', undefined, {
        balance,
        requested: input.points,
      });
    }

    const now = ctx.clock.now();
    const dayKey = toDayKey(now, settings.timezone);
    const id = new ObjectId();
    const doc: RedemptionDoc = {
      _id: id,
      key: redemptionKey(id),
      kind: 'redemption',
      personId: input.personId,
      amount: -input.points,
      date: fromDayKey(dayKey, settings.timezone),
      weekStart: fromDayKey(mondayOf(dayKey), settings.timezone),
      periodStart: null,
      occurrenceId: null,
      taskId: null,
      titleSnapshot: '',
      source: 'live',
      note: input.note,
      centsPerPointSnapshot: settings.centsPerPoint ?? 0,
      currencyCodeSnapshot: settings.currencyCode ?? DEFAULT_CURRENCY_CODE,
      requestId: input.requestId ?? null,
      createdAt: now,
      updatedAt: now,
    };
    const result = await insertRedemption(ctx, doc);
    if (result.inserted) return { doc: result.doc, created: true };
    // Lost a race against the same request key: the winner decides between replay and conflict.
    const winner = input.requestId ? await findRedemptionByRequestId(ctx.db, input.requestId) : null;
    if (!winner) throw idempotencyConflict();
    return replayOrConflict(winner, input);
  });
}

/**
 * Takes a redemption back (requirements 4.12). An administrator can do that at any time; the person it belongs
 * to only on the day it was booked, so a settled payout is not undone silently later. Audited as a delete.
 */
export async function undoRedemption(ctx: AuditContext, id: ObjectId, role: UserRole): Promise<RedemptionDoc> {
  const settings = await getSettings(ctx.db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const current = await findRedemptionById(ctx.db, id);
  if (!current) throw notFound('redemption');
  if (role !== 'admin') {
    if (!current.personId.equals(ctx.actorId)) {
      throw new HttpError(403, 'permission_denied', 'Only the person it belongs to or an administrator can undo a redemption');
    }
    if (toDayKey(current.date, settings.timezone) !== toDayKey(ctx.clock.now(), settings.timezone)) {
      throw new HttpError(403, 'redemption_locked', 'A redemption can only be undone on the day it was booked, or by an administrator');
    }
  }
  const removed = await deleteRedemption(ctx, id);
  if (!removed) throw notFound('redemption');
  return removed;
}
