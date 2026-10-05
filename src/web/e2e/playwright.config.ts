import { defineConfig, devices } from '@playwright/test'

/**
 * End-to-end tests run against a RUNNING stack (they are not part of `npm test`):
 *   docker compose up -d
 *   dotnet run --project src/Helpdesk.Host          (Development: dev sign-in + migrations on start)
 *   npm --prefix src/web run dev                    (http://localhost:5173, proxies /api)
 * then: npm --prefix src/web run e2e
 * Override the target with E2E_BASE_URL. The config deliberately starts no web server.
 */
export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
  },
  // E2E_BROWSER_CHANNEL=chrome|msedge reuses an installed browser instead of `playwright install chromium`.
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], channel: process.env.E2E_BROWSER_CHANNEL || undefined } }],
})
