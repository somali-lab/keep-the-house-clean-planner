#!/usr/bin/env node
/**
 * Docker smoke test of the .NET image. Builds docker/Dockerfile.dotnet and starts it with a throwaway
 * single-node Mongo replica set through docker-compose.yml plus docker/docker-compose.dotnet-smoke.yml, under its own
 * project name, port, image tag and volumes (so a real installation is never touched), checks health, the web app and
 * the Problem Details error shape and a rendered PDF, and always removes the stack and its volumes again.
 * Usage: node scripts/smoke-dotnet.mjs   (SMOKE_PORT overrides the port, default 3200)
 */
import { spawn, spawnSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const PROJECT = 'huishoudplanner-smoke-dotnet';
const PORT = process.env.SMOKE_PORT ?? '3200';
const BASE_URL = `http://127.0.0.1:${PORT}`;
const HEALTHY_TIMEOUT_MS = 5 * 60_000;
const SERVICES = ['app', 'mongo'];

const composeEnv = { ...process.env, APP_PORT: PORT, APP_IMAGE_TAG: 'smoke-dotnet' };

const composeArgs = (args) => [
  'compose',
  '-p',
  PROJECT,
  '-f',
  'docker-compose.yml',
  '-f',
  'docker/docker-compose.dotnet-smoke.yml',
  ...args,
];

function compose(args, { capture = false } = {}) {
  const result = spawnSync('docker', composeArgs(args), {
    cwd: ROOT,
    env: composeEnv,
    stdio: capture ? ['ignore', 'pipe', 'inherit'] : 'inherit',
    encoding: 'utf8',
  });
  if (result.error) throw result.error;
  if (result.status !== 0)
    throw new Error(`docker compose ${args.join(' ')} exited with code ${result.status}`);
  return result.stdout ?? '';
}

let upProcess;
/** The long `up --build` runs asynchronously so a SIGINT/SIGTERM handler can still run while it builds. */
function composeUp(args) {
  return new Promise((resolvePromise, reject) => {
    upProcess = spawn('docker', composeArgs(args), {
      cwd: ROOT,
      env: composeEnv,
      stdio: 'inherit',
    });
    upProcess.on('error', reject);
    upProcess.on('exit', (code) =>
      code === 0
        ? resolvePromise()
        : reject(new Error(`docker compose ${args.join(' ')} exited with code ${code}`)),
    );
  });
}

const step = (message) => console.log(`\n> ${message}`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function assert(condition, message) {
  if (!condition) throw new Error(`check failed: ${message}`);
}

/** Waits for the app healthcheck (docker compose ps reports one JSON object per line). */
async function waitHealthy() {
  const deadline = Date.now() + HEALTHY_TIMEOUT_MS;
  while (Date.now() < deadline) {
    const services = compose(['ps', '--format', 'json'], { capture: true })
      .split(/\r?\n/)
      .filter((line) => line.trim().startsWith('{'))
      .map((line) => JSON.parse(line));
    const app = services.find((s) => s.Service === 'app');
    if (app?.Health === 'healthy') return;
    if (app && app.State !== 'running') throw new Error(`app container is "${app.State}"`);
    await sleep(2000);
  }
  throw new Error('app container did not become healthy');
}

async function get(path) {
  const res = await fetch(`${BASE_URL}${path}`);
  return { res, text: await res.text() };
}

async function checks() {
  step('GET /api/v2/health');
  const health = await get('/api/v2/health');
  assert(
    health.res.status === 200,
    `health answers 200 (got ${health.res.status}: ${health.text})`,
  );
  const body = JSON.parse(health.text);
  assert(
    body.status === 'ok' && body.database === 'ok',
    `health body is ok/ok (got ${health.text})`,
  );
  console.log(`  ${health.text}`);

  step('GET / serves index.html');
  const index = await get('/');
  assert(index.res.status === 200, `/ answers 200 (got ${index.res.status})`);
  assert((index.res.headers.get('content-type') ?? '').startsWith('text/html'), '/ is text/html');
  assert(/<div id="root"|<html/i.test(index.text), '/ looks like the web app index.html');

  step('GET /rooms (SPA deep link) serves index.html');
  const deep = await get('/rooms/some/deep/link');
  assert(deep.res.status === 200, `deep link answers 200 (got ${deep.res.status})`);
  assert(
    (deep.res.headers.get('content-type') ?? '').startsWith('text/html'),
    'deep link is text/html',
  );
  assert(deep.text === index.text, 'deep link serves the same index.html');

  step('GET /api/v2/x is a Problem Details 404');
  const unknown = await get('/api/v2/x');
  assert(unknown.res.status === 404, `unknown api route answers 404 (got ${unknown.res.status})`);
  const type = unknown.res.headers.get('content-type') ?? '';
  assert(
    type.startsWith('application/problem+json'),
    `content-type is application/problem+json (got ${type})`,
  );

  step('GET /api/v2/export/pdf/due renders a PDF (fontconfig and a font are in the image)');
  const pdf = await fetch(`${BASE_URL}/api/v2/export/pdf/due`);
  const pdfBytes = Buffer.from(await pdf.arrayBuffer());
  assert(pdf.status === 200, `pdf export answers 200 (got ${pdf.status})`);
  const pdfType = pdf.headers.get('content-type') ?? '';
  assert(pdfType.startsWith('application/pdf'), `content-type is application/pdf (got ${pdfType})`);
  assert(
    /^attachment; filename="achterstand-\d{4}-\d{2}-\d{2}\.pdf"$/.test(
      pdf.headers.get('content-disposition') ?? '',
    ),
    `content-disposition names the file (got ${pdf.headers.get('content-disposition')})`,
  );
  assert(pdfBytes.subarray(0, 5).toString('latin1') === '%PDF-', 'the body starts with %PDF-');
  console.log(`  ${pdfBytes.length} bytes`);
}

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    console.error(`
Received ${signal}: removing the smoke stack`);
    upProcess?.kill();
    try {
      compose(['down', '-v', '--remove-orphans']);
    } catch (error) {
      console.error(error instanceof Error ? error.message : String(error));
    }
    process.exit(130);
  });
}

let failed = false;
try {
  step('docker compose up -d --build (app, mongo)');
  await composeUp(['up', '-d', '--build', ...SERVICES]);
  step('waiting until the app is healthy');
  await waitHealthy();
  await checks();
  console.log('\nOK: .NET smoke test passed');
} catch (error) {
  failed = true;
  console.error(
    `\nFAILED: .NET smoke test: ${error instanceof Error ? error.message : String(error)}`,
  );
  try {
    compose(['logs', '--no-color', '--tail', '80', ...SERVICES]);
  } catch {
    // Logs are a courtesy; the failure above is what matters.
  }
} finally {
  step('docker compose down');
  try {
    compose(['down', '-v', '--remove-orphans']);
  } catch (error) {
    failed = true;
    console.error(error instanceof Error ? error.message : String(error));
  }
}
process.exit(failed ? 1 : 0);
