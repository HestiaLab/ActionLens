import { defineConfig, devices } from '@playwright/test';

/**
 * Gate 2's browser evidence. The four specs here are the minimum set agreed in
 * docs/review-v1-grill.md (decision Q3) — the assertions that the bUnit suite,
 * which runs on the JS-degraded StubEditorModule, structurally cannot make.
 *
 * The webServer command is the same one a developer runs by hand. A fresh port
 * is used so a stale dev server cannot serve an older build.
 */
export default defineConfig({
  testDir: './tests/e2e',
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['junit', { outputFile: 'test-results/e2e-junit.xml' }]] : 'list',
  timeout: 30_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL: process.env.ACTIONLENS_BASE_URL ?? 'http://127.0.0.1:5179',
    // Traces are large and the suite is small: keep them only when CI retries a
    // failing test. A green run leaves just .last-run.json in test-results/.
    trace: 'on-first-retry',
    ...devices['Desktop Chrome'],
  },
  webServer: {
    command: 'dotnet run --project src/ActionLens.App --urls http://127.0.0.1:5179',
    url: 'http://127.0.0.1:5179',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
    stdout: 'pipe',
    stderr: 'pipe',
  },
});
