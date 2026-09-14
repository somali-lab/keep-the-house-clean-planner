import type { Db, ObjectId } from 'mongodb';
import { findRoomById } from '../data/rooms.ts';
import { getSettings } from '../data/settings.ts';
import { findUserById } from '../data/users.ts';
import { HttpError, type FieldIssue } from '../http/errors.ts';
import { findInterval } from './intervals.ts';

export interface TaskReferences {
  roomId?: ObjectId;
  intervalKey?: string;
  /** null = "wie dan ook"; always valid. */
  defaultAssigneeId?: ObjectId | null;
}

/** Checks that referenced room, interval and assignee exist; throws 400 naming the fields. */
export async function assertTaskReferences(db: Db, refs: TaskReferences): Promise<void> {
  const issues: FieldIssue[] = [];

  if (refs.roomId) {
    const room = await findRoomById(db, refs.roomId);
    if (!room) issues.push({ field: 'roomId', message: 'unknown_room' });
    else if (!room.active) issues.push({ field: 'roomId', message: 'inactive_room' });
  }

  if (refs.intervalKey !== undefined) {
    const settings = await getSettings(db);
    if (!settings || !findInterval(settings.intervals, refs.intervalKey)) {
      issues.push({ field: 'intervalKey', message: 'unknown_interval' });
    }
  }

  if (refs.defaultAssigneeId) {
    const user = await findUserById(db, refs.defaultAssigneeId);
    if (!user) issues.push({ field: 'defaultAssigneeId', message: 'unknown_user' });
    else if (!user.active) issues.push({ field: 'defaultAssigneeId', message: 'inactive_user' });
  }

  if (issues.length > 0) {
    throw new HttpError(400, 'validation_error', 'Invalid references', issues);
  }
}
