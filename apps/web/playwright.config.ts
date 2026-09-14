import { defineConfig, devices } from '@playwright/test';

/**
 * E2E against the built web app, served by the real server with NODE_ENV=test
 * and APP_FAKE_NOW. Every test starts its own server on its own database
 * (see e2e/fixtures.ts), so tests never share state.
 */
export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.spec.ts',
  globalSetup: './e2e/globalSetup.ts',
  fullyParallel: false,
  workers: 1,
  timeout: 90_000,
  expect: { timeout: 10_000 },
  retries: 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    ...devices['Desktop Chrome'],
    viewport: { width: 1400, height: 1000 },
    locale: 'nl-NL',
    timezoneId: 'Europe/Amsterdam',
    // The offline queue lives in the page; a service worker would only add cache timing to the tests.
    serviceWorkers: 'block',
    acceptDownloads: true,
    trace: 'retain-on-failure',
  },
});
