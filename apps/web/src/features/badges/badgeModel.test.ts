import { describe, expect, it } from 'vitest';
import { LIMITS, makeBadge } from '../../test/fixtures.ts';
import type { Limits } from '../../api/v2/queries.ts';
import {
  badgesWithProgress,
  buildBadgeSave,
  checkImageFile,
  EMPTY_BADGE_FORM,
  formFromBadge,
  limitedNames,
  parseThreshold,
  progressText,
  readImageFile,
  ruleText,
  type BadgeForm,
} from './badgeModel.ts';

/** The badge limits as the web app reads them from `GET /api/v2/meta/limits`. */
const BADGE_LIMITS: Limits['badges'] = {
  maxNameLength: LIMITS.badges.maxNameLength,
  maxDescriptionLength: LIMITS.badges.maxDescriptionLength,
  maxImageBytes: LIMITS.badges.maxImageBytes,
  imageTypes: LIMITS.badges.imageTypes,
  maxThreshold: LIMITS.badges.maxThreshold,
  maxOnTimeWeeksThreshold: LIMITS.badges.maxOnTimeWeeksThreshold,
};

const PNG = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0]);
const file = (bytes: Uint8Array | string, name: string, type: string) => new File([bytes as BlobPart], name, { type });

const form = (overrides: Partial<BadgeForm> = {}): BadgeForm => ({ ...EMPTY_BADGE_FORM, name: 'Toiletjuffrouw', ...overrides });

describe('parseThreshold', () => {
  it.each([
    ['10', 'executions', 10],
    [' 7 ', 'minutes', 7],
    ['100000', 'minutes', 100_000],
    ['1000', 'onTimeWeeks', 1000],
  ] as const)('reads %s as %d for %s', (text, type, value) => {
    expect(parseThreshold(text, type, BADGE_LIMITS)).toBe(value);
  });

  it.each([
    ['', 'executions'],
    ['0', 'executions'],
    ['-3', 'executions'],
    ['2.5', 'executions'],
    ['tien', 'executions'],
    ['100001', 'minutes'],
    ['1001', 'onTimeWeeks'],
  ] as const)('refuses %j for %s', (text, type) => {
    expect(parseThreshold(text, type, BADGE_LIMITS)).toBeNull();
  });

  it('takes the maxima from the limits of the server', () => {
    const limits = { ...BADGE_LIMITS, maxThreshold: 50, maxOnTimeWeeksThreshold: 8 };
    expect(parseThreshold('50', 'executions', limits)).toBe(50);
    expect(parseThreshold('51', 'executions', limits)).toBeNull();
    expect(parseThreshold('9', 'onTimeWeeks', limits)).toBeNull();
  });
});

describe('buildBadgeSave', () => {
  it('builds the request of a new badge with its tasks in a stable order and the chosen image', () => {
    const save = buildBadgeSave(
      form({
        name: '  Toiletjuffrouw ',
        description: ' Veel schoongemaakt ',
        threshold: '10',
        taskIds: ['b2', 'a1'],
        image: { kind: 'new', contentType: 'image/png', data: 'AAAA', previewUrl: 'x', fileName: 'a.png' },
      }),
      BADGE_LIMITS,
    );
    const request = {
      name: 'Toiletjuffrouw',
      description: 'Veel schoongemaakt',
      rule: { type: 'executions', taskIds: ['a1', 'b2'], threshold: 10 },
      active: true,
      image: { contentType: 'image/png', data: 'AAAA' },
    };
    expect(save).toEqual({ ok: true, create: request, patch: request });
  });

  it('leaves a kept image out of the change, sends null for a removed one, and drops the tasks of an on-time-weeks rule', () => {
    const kept = buildBadgeSave(form({ image: { kind: 'keep' }, ruleType: 'onTimeWeeks', threshold: '4', taskIds: ['a1'] }), BADGE_LIMITS);
    expect(kept.ok && kept.patch).toEqual({ name: 'Toiletjuffrouw', description: '', rule: { type: 'onTimeWeeks', threshold: 4 }, active: true });
    const removed = buildBadgeSave(form({ image: { kind: 'none' } }), BADGE_LIMITS);
    expect(removed.ok && removed.patch.image).toBeNull();
    expect(removed.ok && 'image' in removed.create).toBe(false);
  });

  it('reports every field that is wrong', () => {
    expect(buildBadgeSave(form({ name: '  ', description: 'x'.repeat(201), threshold: '0' }), BADGE_LIMITS)).toEqual({
      ok: false,
      errors: { name: 'badges.error.name', description: 'badges.error.description', threshold: 'badges.error.threshold' },
    });
    expect(buildBadgeSave(form({ name: 'x'.repeat(61) }), BADGE_LIMITS)).toMatchObject({ ok: false, errors: { name: 'badges.error.name' } });
    expect(buildBadgeSave(form({ name: 'x'.repeat(60), description: 'x'.repeat(200) }), BADGE_LIMITS).ok).toBe(true);
  });

  it('starts an existing badge from its own values', () => {
    const badge = makeBadge({ id: 'b1', name: 'Dweilkampioen', rule: { type: 'minutes', taskIds: ['t1'], threshold: 300 }, active: false });
    expect(formFromBadge(badge)).toEqual({
      name: 'Dweilkampioen',
      description: '',
      ruleType: 'minutes',
      threshold: '300',
      taskIds: ['t1'],
      active: false,
      image: { kind: 'none' },
    });
  });
});

describe('image files', () => {
  it('checks the type and size the browser reports against the limits of the server', () => {
    expect(checkImageFile({ type: 'image/png', size: 256 * 1024 }, BADGE_LIMITS)).toBeNull();
    expect(checkImageFile({ type: 'image/jpeg', size: 1 }, BADGE_LIMITS)).toBeNull();
    expect(checkImageFile({ type: 'image/webp', size: 1 }, BADGE_LIMITS)).toBeNull();
    expect(checkImageFile({ type: 'image/png', size: 256 * 1024 + 1 }, BADGE_LIMITS)).toBe('badges.error.imageSize');
    expect(checkImageFile({ type: 'image/svg+xml', size: 100 }, BADGE_LIMITS)).toBe('badges.error.imageType');
    expect(checkImageFile({ type: 'image/gif', size: 100 }, BADGE_LIMITS)).toBe('badges.error.imageType');
    expect(checkImageFile({ type: '', size: 100 }, BADGE_LIMITS)).toBe('badges.error.imageType');
    expect(checkImageFile({ type: 'image/png', size: 600 }, { ...BADGE_LIMITS, maxImageBytes: 500 })).toBe('badges.error.imageSize');
  });

  it('reads a file as base64 together with its type; the bytes themselves are checked by the server', async () => {
    const result = await readImageFile(file(PNG, 'a.png', 'image/png'), BADGE_LIMITS);
    expect(result).toEqual({ ok: true, contentType: 'image/png', data: btoa(String.fromCharCode(...PNG)) });
  });

  it('refuses a file of another type and a file that is too large without reading it', async () => {
    expect(await readImageFile(file('<svg xmlns="http://www.w3.org/2000/svg"/>', 'a.svg', 'image/svg+xml'), BADGE_LIMITS)).toEqual({
      ok: false,
      error: 'badges.error.imageType',
    });
    expect(await readImageFile(file(new Uint8Array(256 * 1024 + 1), 'big.png', 'image/png'), BADGE_LIMITS)).toEqual({
      ok: false,
      error: 'badges.error.imageSize',
    });
  });
});

describe('describing a rule and showing progress', () => {
  const names = new Map([
    ['t1', 'Toilet'],
    ['t2', 'Fonteintje'],
  ]);

  it('names the tasks of a rule, leaves out deleted ones and says "all tasks" through an empty list', () => {
    expect(ruleText({ type: 'executions', taskIds: ['t1', 'gone'], threshold: 10 }, names)).toEqual({ key: 'badges.rule.executions', count: 10, tasks: ['Toilet'] });
    expect(ruleText({ type: 'minutes', taskIds: [], threshold: 300 }, names)).toEqual({ key: 'badges.rule.minutes', count: 300, tasks: [] });
    expect(ruleText({ type: 'onTimeWeeks', taskIds: [], threshold: 4 }, names)).toEqual({ key: 'badges.rule.onTimeWeeks', count: 4, tasks: [] });
    expect(limitedNames(['a', 'b', 'c', 'd', 'e'])).toEqual({ shown: ['a', 'b', 'c'], more: 2 });
    expect(limitedNames(['a'])).toEqual({ shown: ['a'], more: 0 });
  });

  it('shows progress as "7/10" and never above the threshold', () => {
    expect(progressText(7, 10)).toBe('7/10');
    expect(progressText(25, 10)).toBe('10/10');
    expect(progressText(0, 3)).toBe('0/3');
  });

  it('lists earned badges first by the day they were earned, then the others by how close they are, and leaves inactive ones out', () => {
    const badges = [
      makeBadge({ id: 'b1', name: 'Ver weg' }),
      makeBadge({ id: 'b2', name: 'Bijna' }),
      makeBadge({ id: 'b3', name: 'Later behaald' }),
      makeBadge({ id: 'b4', name: 'Eerder behaald' }),
      makeBadge({ id: 'b5', name: 'Uit', active: false }),
    ];
    const views = badgesWithProgress(badges, [
      { badgeId: 'b1', current: 1, threshold: 10, awardedAt: null },
      { badgeId: 'b2', current: 9, threshold: 10, awardedAt: null },
      { badgeId: 'b3', current: 10, threshold: 10, awardedAt: '2026-09-18T08:00:00.000Z' },
      { badgeId: 'b4', current: 10, threshold: 10, awardedAt: '2026-09-16T08:00:00.000Z' },
    ]);
    expect(views.map((view) => view.badge.name)).toEqual(['Eerder behaald', 'Later behaald', 'Bijna', 'Ver weg']);
  });
});
