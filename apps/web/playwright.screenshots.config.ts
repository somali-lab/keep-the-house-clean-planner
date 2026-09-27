import { defineConfig, devices } from '@playwright/test';

/**
 * Regenerates the README screenshots in docs/screenshots.
 * Separate from playwright.config.ts so a normal E2E run never rewrites the images.
 * The viewport is the published image size; English and light mode match the README.
 */
export default defineConfig({
  testDir: './e2e',
  testMatch: '**/screenshots.capture.ts',
  globalSetup: './e2e/screenshotsSetup.ts',
  workers: 1,
  timeout: 180_000,
  reporter: [['list']],
  use: {
    ...devices['Desktop Chrome'],
    viewport: { width: 1400, height: 1000 },
    deviceScaleFactor: 1,
    locale: 'en-GB',
    timezoneId: 'Europe/Amsterdam',
    colorScheme: 'light',
    serviceWorkers: 'block',
  },
});
