import { describe, expect, it } from 'vitest';
import {
  addExampleBadgesInputSchema,
  auditEntitySchema,
  badgeSchema,
  createBadgeInputSchema,
  evaluateBadgeRule,
  EXAMPLE_BADGES,
  MAX_BADGE_IMAGE_BASE64_LENGTH,
  MAX_BADGE_IMAGE_BYTES,
  ruleCovers,
  sniffBadgeImageType,
  updateBadgeInputSchema,
  type BadgeExecution,
  type BadgeRule,
} from './index.ts';

const ID = '0123456789abcdef01234567';
const OTHER = '89abcdef0123456789abcdef';

const run = (id: string, taskId: string | null, at: string, minutes = 10): BadgeExecution => ({ id, taskId, minutes, at });

describe('evaluateBadgeRule', () => {
  const runs = [
    run('e3', ID, '2026-09-18T08:00:00.000Z', 30),
    run('e1', ID, '2026-09-16T08:00:00.000Z', 10),
    run('e2', OTHER, '2026-09-17T08:00:00.000Z', 20),
    run('e4', null, '2026-09-19T08:00:00.000Z', 40),
  ];

  it('counts executions of the chosen tasks and awards at the moment of the threshold-th one, whatever order they come in', () => {
    const rule: BadgeRule = { type: 'executions', taskIds: [ID], threshold: 2 };
    expect(evaluateBadgeRule(rule, runs, [])).toEqual({ current: 2, awardedAt: '2026-09-18T08:00:00.000Z' });
    expect(evaluateBadgeRule(rule, [...runs].reverse(), [])).toEqual({ current: 2, awardedAt: '2026-09-18T08:00:00.000Z' });
    expect(evaluateBadgeRule({ ...rule, threshold: 3 }, runs, [])).toEqual({ current: 2, awardedAt: null });
  });

  it('counts every task, a one-off task included, when none is chosen; a one-off task never counts for chosen tasks', () => {
    const all: BadgeRule = { type: 'executions', taskIds: [], threshold: 4 };
    expect(evaluateBadgeRule(all, runs, [])).toEqual({ current: 4, awardedAt: '2026-09-19T08:00:00.000Z' });
    expect(ruleCovers(all, null)).toBe(true);
    const chosen: BadgeRule = { type: 'executions', taskIds: [ID, OTHER], threshold: 1 };
    expect(ruleCovers(chosen, null)).toBe(false);
    expect(evaluateBadgeRule(chosen, [run('e4', null, '2026-09-19T08:00:00.000Z')], [])).toEqual({ current: 0, awardedAt: null });
  });

  it('adds up minutes and awards at the execution that reaches the threshold, with the total beyond it still reported', () => {
    const rule: BadgeRule = { type: 'minutes', taskIds: [], threshold: 55 };
    // 10 + 20 + 30 = 60 at the third execution in time order (e1, e2, e3), 100 in total.
    expect(evaluateBadgeRule(rule, runs, [])).toEqual({ current: 100, awardedAt: '2026-09-18T08:00:00.000Z' });
    expect(evaluateBadgeRule({ ...rule, threshold: 100 }, runs, [])).toEqual({ current: 100, awardedAt: '2026-09-19T08:00:00.000Z' });
    expect(evaluateBadgeRule({ ...rule, threshold: 101 }, runs, [])).toEqual({ current: 100, awardedAt: null });
  });

  it('breaks ties between executions at the same moment by id, so the answer never depends on input order', () => {
    const same = [run('b', ID, '2026-09-16T08:00:00.000Z', 5), run('a', ID, '2026-09-16T08:00:00.000Z', 50)];
    const rule: BadgeRule = { type: 'minutes', taskIds: [], threshold: 40 };
    expect(evaluateBadgeRule(rule, same, [])).toEqual(evaluateBadgeRule(rule, [...same].reverse(), []));
  });

  it('counts on-time weeks by the date of the threshold-th week, and ignores executions', () => {
    const rule: BadgeRule = { type: 'onTimeWeeks', threshold: 2 };
    const weeks = ['2026-09-27T00:00:00.000Z', '2026-09-20T00:00:00.000Z', '2026-10-04T00:00:00.000Z'];
    expect(evaluateBadgeRule(rule, runs, weeks)).toEqual({ current: 3, awardedAt: '2026-09-27T00:00:00.000Z' });
    expect(evaluateBadgeRule({ ...rule, threshold: 4 }, runs, weeks)).toEqual({ current: 3, awardedAt: null });
    expect(evaluateBadgeRule(rule, runs, [])).toEqual({ current: 0, awardedAt: null });
  });

  it('is a pure function: the same data always gives the same answer', () => {
    const rule: BadgeRule = { type: 'executions', taskIds: [], threshold: 2 };
    const before = structuredClone(runs);
    expect(evaluateBadgeRule(rule, runs, [])).toEqual(evaluateBadgeRule(rule, runs, []));
    expect(runs).toEqual(before);
  });
});

describe('sniffBadgeImageType', () => {
  it('recognises PNG, JPEG and WebP by their first bytes', () => {
    expect(sniffBadgeImageType(Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0]))).toBe('image/png');
    expect(sniffBadgeImageType(Uint8Array.from([0xff, 0xd8, 0xff, 0xe0]))).toBe('image/jpeg');
    expect(sniffBadgeImageType(new TextEncoder().encode('RIFF\u0001\u0000\u0000\u0000WEBPVP8 '))).toBe('image/webp');
  });

  it('refuses an SVG, text, other RIFF files and files that are too short', () => {
    expect(sniffBadgeImageType(new TextEncoder().encode('<svg xmlns="http://www.w3.org/2000/svg"/>'))).toBeNull();
    expect(sniffBadgeImageType(new TextEncoder().encode('GIF89a'))).toBeNull();
    expect(sniffBadgeImageType(new TextEncoder().encode('RIFF\u0001\u0000\u0000\u0000WAVEfmt '))).toBeNull();
    expect(sniffBadgeImageType(Uint8Array.from([0x89, 0x50]))).toBeNull();
    expect(sniffBadgeImageType(new Uint8Array())).toBeNull();
  });
});

describe('badge schemas', () => {
  const valid = { name: 'Toiletjuffrouw', rule: { type: 'executions', taskIds: [ID], threshold: 10 } };

  it('applies the defaults of a new badge and trims its name', () => {
    expect(createBadgeInputSchema.parse({ ...valid, name: '  Toiletjuffrouw ' })).toEqual({ ...valid, description: '', active: true });
  });

  it('bounds the name, the description and the threshold', () => {
    const bad = (patch: Record<string, unknown>) => createBadgeInputSchema.safeParse({ ...valid, ...patch }).success;
    expect(bad({ name: 'x'.repeat(60) })).toBe(true);
    expect(bad({ name: 'x'.repeat(61) })).toBe(false);
    expect(bad({ name: '   ' })).toBe(false);
    expect(bad({ description: 'x'.repeat(200) })).toBe(true);
    expect(bad({ description: 'x'.repeat(201) })).toBe(false);
    expect(bad({ rule: { type: 'minutes', taskIds: [], threshold: 100_000 } })).toBe(true);
    expect(bad({ rule: { type: 'minutes', taskIds: [], threshold: 100_001 } })).toBe(false);
    expect(bad({ rule: { type: 'executions', taskIds: [], threshold: 0 } })).toBe(false);
    expect(bad({ rule: { type: 'executions', taskIds: [], threshold: 2.5 } })).toBe(false);
    expect(bad({ rule: { type: 'executions', taskIds: ['nope'], threshold: 1 } })).toBe(false);
    expect(bad({ rule: { type: 'onTimeWeeks', threshold: 1000 } })).toBe(true);
    expect(bad({ rule: { type: 'onTimeWeeks', threshold: 1001 } })).toBe(false);
    expect(bad({ rule: { type: 'streak', threshold: 3 } })).toBe(false);
    // An on-time-weeks rule has no tasks.
    expect(createBadgeInputSchema.parse({ ...valid, rule: { type: 'onTimeWeeks', threshold: 4, taskIds: [ID] } }).rule).toEqual({ type: 'onTimeWeeks', threshold: 4 });
  });

  it('accepts PNG, JPEG and WebP images and nothing else, and caps the size of the base64 text at 256 KB of bytes', () => {
    const image = (contentType: string, data = 'AAAA') => createBadgeInputSchema.safeParse({ ...valid, image: { contentType, data } }).success;
    expect(image('image/png')).toBe(true);
    expect(image('image/jpeg')).toBe(true);
    expect(image('image/webp')).toBe(true);
    expect(image('image/svg+xml')).toBe(false);
    expect(image('image/gif')).toBe(false);
    expect(MAX_BADGE_IMAGE_BYTES).toBe(256 * 1024);
    expect(image('image/png', 'A'.repeat(MAX_BADGE_IMAGE_BASE64_LENGTH))).toBe(true);
    expect(image('image/png', 'A'.repeat(MAX_BADGE_IMAGE_BASE64_LENGTH + 1))).toBe(false);
    expect(image('image/png', '')).toBe(false);
  });

  it('lets an update change any part, and remove the image with null', () => {
    expect(updateBadgeInputSchema.parse({})).toEqual({});
    expect(updateBadgeInputSchema.parse({ active: false, image: null })).toEqual({ active: false, image: null });
    expect(updateBadgeInputSchema.safeParse({ name: '' }).success).toBe(false);
  });

  it('describes a badge view without image bytes', () => {
    const view = {
      _id: ID,
      name: 'Toiletjuffrouw',
      description: '',
      rule: { type: 'executions', taskIds: [], threshold: 10 },
      active: true,
      exampleKey: null,
      image: { contentType: 'image/png', size: 70, hash: 'a'.repeat(64), url: `/api/badges/${ID}/image?v=aaaaaaaaaaaa` },
      createdAt: '2026-09-16T08:00:00.000Z',
      updatedAt: '2026-09-16T08:00:00.000Z',
    };
    expect(badgeSchema.safeParse(view).success).toBe(true);
    expect(badgeSchema.safeParse({ ...view, image: { ...view.image, hash: 'xyz' } }).success).toBe(false);
  });

  it('knows the audit entities and the example language', () => {
    expect(auditEntitySchema.safeParse('badge').success).toBe(true);
    expect(auditEntitySchema.safeParse('badgeAward').success).toBe(true);
    expect(addExampleBadgesInputSchema.parse({})).toEqual({ language: 'nl' });
    expect(addExampleBadgesInputSchema.safeParse({ language: 'fr' }).success).toBe(false);
  });
});

describe('EXAMPLE_BADGES', () => {
  it('has three examples with unique stable keys, texts within the limits and valid thresholds', () => {
    expect(EXAMPLE_BADGES.map((example) => example.key)).toEqual(['example:on_time', 'example:toilet', 'example:mop']);
    for (const example of EXAMPLE_BADGES) {
      for (const language of ['nl', 'en'] as const) {
        const { name, description } = example.text[language];
        expect(name.length).toBeGreaterThan(0);
        expect(name.length).toBeLessThanOrEqual(60);
        expect(description.length).toBeLessThanOrEqual(200);
      }
      const rule = example.rule.type === 'onTimeWeeks' ? example.rule : { ...example.rule, taskIds: [] };
      expect(createBadgeInputSchema.safeParse({ name: example.text.nl.name, rule }).success, example.key).toBe(true);
      if (example.taskNamePattern) expect(() => new RegExp(example.taskNamePattern!, 'i')).not.toThrow();
    }
    expect(EXAMPLE_BADGES[1]!.text.nl.name).toBe('Toiletjuffrouw');
    expect(EXAMPLE_BADGES[2]!.text.nl.name).toBe('Dweilkampioen');
  });

  it('matches the usual names of the tasks they are about', () => {
    const matches = (key: string, name: string) => new RegExp(EXAMPLE_BADGES.find((e) => e.key === key)!.taskNamePattern!, 'i').test(name);
    expect(matches('example:toilet', 'Toilet schoonmaken')).toBe(true);
    expect(matches('example:toilet', 'WC poetsen')).toBe(true);
    expect(matches('example:toilet', 'Zwcsdf')).toBe(false);
    expect(matches('example:mop', 'Vloer dweilen')).toBe(true);
    expect(matches('example:mop', 'Mop de keuken')).toBe(true);
    expect(matches('example:mop', 'Stofzuigen')).toBe(false);
  });
});
