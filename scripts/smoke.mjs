#!/usr/bin/env node
/**
 * Docker smoke test. Builds and starts the compose stack under its own project
 * name, port, image tag and backup folder (so a real installation is never
 * touched), walks through the main flow over HTTP and always removes the stack
 * and its volumes again. Usage: node scripts/smoke.mjs
 */
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const PROJECT = 'huishoudplanner-smoke';
const PORT = process.env.SMOKE_PORT ?? '3100';
const BASE_URL = `http://127.0.0.1:${PORT}`;
const BACKUP_DIR_RELATIVE = './backups/smoke';
const BACKUP_DIR = join(ROOT, 'backups', 'smoke');
const TIMEZONE = 'Europe/Amsterdam';
const HEALTHY_TIMEOUT_MS = 5 * 60_000;

const composeEnv = {
  ...process.env,
  APP_PORT: PORT,
  APP_IMAGE_TAG: 'smoke',
  BACKUP_HOST_DIR: BACKUP_DIR_RELATIVE,
};

function compose(args, { capture = false } = {}) {
  const result = spawnSync('docker', ['compose', '-p', PROJECT, ...args], {
    cwd: ROOT,
    env: composeEnv,
    stdio: capture ? ['ignore', 'pipe', 'inherit'] : 'inherit',
    encoding: 'utf8',
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`docker compose ${args.join(' ')} exited with code ${result.status}`);
  return result.stdout ?? '';
}

const step = (message) => console.log(`\n▶ ${message}`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function assert(condition, message) {
  if (!condition) throw new Error(`controle mislukt: ${message}`);
}

async function request(method, path, { body, profile, raw = false } = {}) {
  const res = await fetch(`${BASE_URL}${path}`, {
    method,
    headers: {
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...(profile ? { 'X-Profile-Id': profile } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`${method} ${path} gaf ${res.status}: ${await res.text()}`);
  return raw ? res : res.json();
}

/** Waits for the container healthcheck (docker compose ps reports one JSON object per line). */
async function waitHealthy() {
  const deadline = Date.now() + HEALTHY_TIMEOUT_MS;
  while (Date.now() < deadline) {
    const services = compose(['ps', '--format', 'json'], { capture: true })
      .split(/\r?\n/)
      .filter((line) => line.trim().startsWith('{'))
      .map((line) => JSON.parse(line));
    const app = services.find((s) => s.Service === 'app');
    if (app?.Health === 'healthy') return;
    if (app && app.State !== 'running') throw new Error(`app-container staat op "${app.State}"`);
    await sleep(2000);
  }
  throw new Error('app-container werd niet healthy');
}

/** Day key and weekday (0 = Sunday) of today in the app timezone. */
function today() {
  const parts = Object.fromEntries(
    new Intl.DateTimeFormat('en-CA', { timeZone: TIMEZONE, year: 'numeric', month: '2-digit', day: '2-digit', weekday: 'short' })
      .formatToParts(new Date())
      .map((p) => [p.type, p.value]),
  );
  return {
    dayKey: `${parts.year}-${parts.month}-${parts.day}`,
    weekday: ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].indexOf(parts.weekday),
  };
}

async function walkThrough() {
  step('profiel ophalen');
  const users = await request('GET', '/api/users');
  assert(users.length > 0, 'er is minstens één profiel');
  const profile = users[0]._id;

  step('taak aanmaken');
  const rooms = await request('GET', '/api/rooms');
  const room = rooms.find((r) => !r.virtual) ?? rooms[0];
  const task = await request('POST', '/api/tasks', {
    profile,
    body: { name: 'Smoke-test: aanrecht', roomId: room._id, intervalKey: '1w', durationMinutes: 10 },
  });

  step('slots zetten (vandaag, alle vier de weken)');
  const { dayKey, weekday } = today();
  const plan = await request('POST', '/api/cycle-plans', { profile, body: { name: 'Smoke-test plan' } });
  await request('PUT', `/api/cycle-plans/${plan._id}/slots`, {
    profile,
    body: { slots: [0, 1, 2, 3].map((weekIndex) => ({ taskId: task._id, weekIndex, weekday, assigneeId: profile })) },
  });

  step('plan activeren');
  await request('POST', `/api/cycle-plans/${plan._id}/activate`, { profile });

  step('occurrences van vandaag ophalen');
  const findToday = async () =>
    (await request('GET', `/api/occurrences?from=${dayKey}&to=${dayKey}`)).find((o) => o.taskId === task._id);
  const occurrence = await findToday();
  assert(occurrence?.status === 'open', `de taak staat vandaag (${dayKey}) open`);

  step('afvinken');
  await request('PATCH', `/api/occurrences/${occurrence._id}`, { profile, body: { action: 'complete' } });
  assert((await findToday())?.status === 'done', 'de taak is afgevinkt');

  step('PDF downloaden');
  const pdf = await request('GET', `/api/export/pdf/day?date=${dayKey}`, { raw: true });
  const contentType = pdf.headers.get('content-type') ?? '';
  const size = (await pdf.arrayBuffer()).byteLength;
  assert(contentType.startsWith('application/pdf'), `content-type is application/pdf (kreeg ${contentType})`);
  assert(size > 1024, `PDF is groter dan 1 KB (kreeg ${size} bytes)`);

  step('backup-container starten');
  const expiredArchive = join(BACKUP_DIR, 'huishoudplanner-20200101.archive.gz');
  writeFileSync(expiredArchive, 'old backup');
  compose(['run', '--rm', 'backup', 'once']);
  const archive = join(BACKUP_DIR, `huishoudplanner-${dayKey.replaceAll('-', '')}.archive.gz`);
  assert(existsSync(archive) && statSync(archive).size > 0, `archief staat in ${BACKUP_DIR_RELATIVE}`);
  assert(!existsSync(expiredArchive), 'verlopen backup is verwijderd');
}

let failed = false;
mkdirSync(BACKUP_DIR, { recursive: true });
try {
  step('docker compose up -d --build');
  compose(['up', '-d', '--build']);
  step('wachten tot de app healthy is');
  await waitHealthy();
  await walkThrough();
  console.log('\n✔ Smoke-test geslaagd');
} catch (error) {
  failed = true;
  console.error(`\n✖ Smoke-test mislukt: ${error instanceof Error ? error.message : String(error)}`);
  try {
    compose(['logs', '--no-color', '--tail', '80', 'app']);
  } catch {
    // Logs are a courtesy; the failure above is what matters.
  }
} finally {
  step('docker compose down');
  try {
    compose(['down', '-v']);
  } catch (error) {
    failed = true;
    console.error(error instanceof Error ? error.message : String(error));
  }
  rmSync(BACKUP_DIR, { recursive: true, force: true });
}
process.exit(failed ? 1 : 0);
