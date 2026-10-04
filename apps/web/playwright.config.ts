import { defineConfig, devices } from '@playwright/test';

/**
 * E2E against the built web app, served by the real .NET host (published by e2e/globalSetup.ts) with
 * ASPNETCORE_ENVIRONMENT=test and APP_FAKE_NOW, on a throwaway MongoDB replica set in Docker. Every test
 * starts its own host process on its own database (see e2e/fixtures.ts), so tests never share state.
 */
export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.spec.ts',
  globalSetup: './e2e/globalSetup.ts',
  fullyParallel: false,
  // Every test owns its server, port and database, so spec files can run side by side; CI runners have 4 vCPUs.
  workers: process.env.CI ? 4 : 1,
  timeout: 90_000,
  expect: { timeout: 10_000 },
  retries: process.env.CI ? 1 : 0,
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
