import { defineConfig } from '@playwright/test'

// Browser tests (web/e2e). They run against a running copy of JBS Contracts with header sign-in and a fresh, empty database:
// the tests load the fictional demo data first. Never point them at QA or Production.
//   E2E_BASE_URL=http://localhost:5091 npm run e2e
// CI starts the API itself (ci.yml, "Browser tests").
const phone = { width: 375, height: 812 }
const desktop = { width: 1280, height: 800 }

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : 4,
  timeout: 45_000,
  expect: { timeout: 10_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:5091',
    locale: 'en-AU',
    timezoneId: 'Australia/Brisbane',
    trace: 'off',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'phone-light', use: { viewport: phone, colorScheme: 'light', hasTouch: true } },
    { name: 'phone-dark', use: { viewport: phone, colorScheme: 'dark', hasTouch: true } },
    { name: 'desktop-light', use: { viewport: desktop, colorScheme: 'light' } },
    { name: 'desktop-dark', use: { viewport: desktop, colorScheme: 'dark' } },
  ],
})
