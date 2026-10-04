import { describe, expect, it } from 'vitest';
import {
  buildCommands,
  checkRails,
  compareCounts,
  composeBase,
  DEFAULT_SOURCE_DB,
  DEFAULT_TARGET_DB,
  parseArgs,
  UsageError,
  isLive,
  judgeCopy,
  parseServiceStates,
} from './parallelCopy.ts';

describe('arguments', () => {
  it('default to the live database as source and huishoudplanner_next as target, asking for confirmation', () => {
    expect(parseArgs([])).toMatchObject({
      source: DEFAULT_SOURCE_DB,
      target: DEFAULT_TARGET_DB,
      force: false,
      yes: false,
      dryRun: false,
      project: undefined,
      files: [],
    });
  });

  it('read every flag, with a separate or an inline value', () => {
    expect(
      parseArgs([
        '--source',
        'a',
        '--target=b',
        '--force',
        '--yes',
        '--dry-run',
        '-p',
        'proj',
        '-f',
        'x.yml',
        '--file=y.yml',
      ]),
    ).toMatchObject({
      source: 'a',
      target: 'b',
      force: true,
      yes: true,
      dryRun: true,
      project: 'proj',
      files: ['x.yml', 'y.yml'],
    });
  });

  it('refuse an unknown flag and a flag without a value', () => {
    expect(() => parseArgs(['--wipe'])).toThrow(UsageError);
    expect(() => parseArgs(['--target'])).toThrow('--target needs a value');
    expect(() => parseArgs(['--target', '--yes'])).toThrow('--target needs a value');
  });
});

describe('safety rails', () => {
  const rails = (over: Partial<Parameters<typeof checkRails>[0]> = {}) =>
    checkRails({ source: DEFAULT_SOURCE_DB, target: DEFAULT_TARGET_DB, force: false, ...over });

  it('allow the default copy', () => {
    expect(rails()).toEqual([]);
  });

  it('refuse a target that is not huishoudplanner_next unless forced', () => {
    expect(rails({ target: 'scratch' })).toHaveLength(1);
    expect(rails({ target: 'scratch' })[0]).toContain('--force');
    expect(rails({ target: 'scratch', force: true })).toEqual([]);
  });

  it('never restore over the source, not even forced', () => {
    expect(rails({ target: DEFAULT_SOURCE_DB, force: true }).join(' ')).toContain('same database');
    expect(rails({ source: 'a', target: 'a', force: true }).join(' ')).toContain('same database');
  });

  it('compare database names case-insensitively', () => {
    expect(rails({ target: 'HUISHOUDPLANNER', force: true }).join(' ')).toContain('same database');
    expect(rails({ source: 'other', target: 'Huishoudplanner', force: true }).join(' ')).toContain(
      'live database',
    );
    expect(rails({ target: 'Huishoudplanner_Next' })).toHaveLength(1);
  });

  it('never restore into the live database, not even forced', () => {
    expect(rails({ source: 'other', target: DEFAULT_SOURCE_DB, force: true }).join(' ')).toContain(
      'live database',
    );
  });

  it('refuse names that are not plain database names', () => {
    expect(rails({ target: 'x; drop', force: true }).join(' ')).toContain(
      'not a plain MongoDB database name',
    );
    expect(rails({ source: '../x' }).join(' ')).toContain('not a plain MongoDB database name');
    expect(rails({ target: '', force: true }).join(' ')).toContain(
      'not a plain MongoDB database name',
    );
  });
});

describe('commands', () => {
  const options = (over = {}) => ({ ...parseArgs([]), ...over });

  it('read the source with mongodump only and write the target with mongorestore only, inside the mongo service', () => {
    const c = buildCommands(options());
    expect(c.dump.slice(0, 5)).toEqual(['compose', 'exec', '-T', 'mongo', 'mongodump']);
    expect(c.dump).toEqual(expect.arrayContaining(['--db', DEFAULT_SOURCE_DB, '--archive']));
    expect(c.dump.join(' ')).not.toContain(DEFAULT_TARGET_DB);
    expect(c.restore.slice(0, 5)).toEqual(['compose', 'exec', '-T', 'mongo', 'mongorestore']);
    expect(c.restore).toEqual(
      expect.arrayContaining([
        '--nsFrom',
        `${DEFAULT_SOURCE_DB}.*`,
        '--nsTo',
        `${DEFAULT_TARGET_DB}.*`,
        '--nsInclude',
        `${DEFAULT_SOURCE_DB}.*`,
      ]),
    );
    expect(c.restore).not.toContain('--drop');
  });

  it('drop only the target database, and count the source read-only', () => {
    const c = buildCommands(options());
    expect(c.dropTarget.join(' ')).toContain(
      `db.getSiblingDB("${DEFAULT_TARGET_DB}").dropDatabase()`,
    );
    expect(c.dropTarget.join(' ')).not.toContain(`"${DEFAULT_SOURCE_DB}"`);
    const count = c.count(DEFAULT_SOURCE_DB).join(' ');
    expect(count).toContain('countDocuments');
    expect(count).not.toMatch(/drop|insert|update|delete|remove/i);
  });

  it('pass the project and files to docker compose', () => {
    expect(composeBase({ project: 'p', files: ['a.yml', 'b.yml'] })).toEqual([
      'compose',
      '-p',
      'p',
      '-f',
      'a.yml',
      '-f',
      'b.yml',
    ]);
    const c = buildCommands(options({ project: 'p' }));
    expect(c.states).toEqual([
      'compose',
      '-p',
      'p',
      '--profile',
      'parallel',
      'ps',
      '--all',
      '--format',
      'json',
    ]);
    expect(c.dump.slice(0, 4)).toEqual(['compose', '-p', 'p', 'exec']);
  });
});

describe('verification', () => {
  it('finds a complete copy equal', () => {
    expect(compareCounts({ users: 2, rooms: 7 }, { rooms: 7, users: 2 })).toEqual([]);
  });

  it('reports a missing collection, another count and a collection only in the target', () => {
    expect(
      compareCounts({ users: 2, rooms: 7, tasks: 1 }, { users: 3, rooms: 7, pointGuards: 1 }),
    ).toEqual([
      'collection tasks is missing in the target (source has 1 documents)',
      'collection users: source 2, target 3',
      'collection pointGuards exists only in the target (1 documents)',
    ]);
  });
});

describe('service states', () => {
  const ps = [
    '{"Service":"mongo","State":"running"}',
    '{"Service":"app-next","State":"Restarting"}',
  ].join('\n');

  it('are read from the json lines of docker compose ps, also as one array', () => {
    expect(parseServiceStates(ps)).toEqual({ mongo: 'running', 'app-next': 'restarting' });
    expect(parseServiceStates('[{"Service":"mongo","State":"running"}]')).toEqual({
      mongo: 'running',
    });
    expect(parseServiceStates('')).toEqual({});
  });

  it('count every state but exited and dead as live, and a missing container as not', () => {
    for (const state of ['running', 'restarting', 'created', 'paused', 'removing'])
      expect(isLive(state)).toBe(true);
    for (const state of ['exited', 'dead', undefined]) expect(isLive(state)).toBe(false);
  });
});

describe('judging a copy', () => {
  const before = { users: 2, tasks: 10 };

  it('accepts an exact copy', () => {
    expect(judgeCopy(before, { ...before }, { ...before }).code).toBe(0);
  });

  it('accepts a difference that lies within the movement of the source itself', () => {
    const verdict = judgeCopy(before, { users: 2, tasks: 11 }, { users: 2, tasks: 12 });
    expect(verdict.code).toBe(0);
    expect(verdict.problems).toHaveLength(1);
    expect(verdict.sourceMoved).toHaveLength(1);
  });

  it('fails a copy that differs while the source stood still', () => {
    expect(judgeCopy(before, { users: 2, tasks: 9 }, { ...before }).code).toBe(1);
  });

  it('fails a copy that lost something the source did not move on', () => {
    // tasks moved on the source, but users is missing in the target: not explained by the movement.
    expect(judgeCopy(before, { tasks: 11 }, { users: 2, tasks: 12 }).code).toBe(1);
    expect(judgeCopy(before, { users: 1, tasks: 11 }, { users: 2, tasks: 12 }).code).toBe(1);
  });
});
