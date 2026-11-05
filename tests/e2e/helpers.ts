import { test, expect, type Page } from '@playwright/test';

/**
 * Shared helpers for the Gate 2 browser suite. Every spec waits for the app to
 * finish its first analysis rather than for a fixed timeout: the WASM runtime
 * downloads on first paint and the debounce is 250 ms.
 */

/** Collects console errors and failed responses for the duration of a test. */
export function watchPage(page: Page) {
  const consoleErrors: string[] = [];
  const failedRequests: string[] = [];
  const resourceEntries: PerformanceEntry[] = [];

  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('pageerror', (error) => consoleErrors.push(`pageerror: ${error.message}`));
  page.on('requestfailed', (request) =>
    failedRequests.push(`${request.url()}: ${request.failure()?.errorText ?? 'failed'}`),
  );
  page.on('response', (response) => {
    if (response.status() >= 400) {
      failedRequests.push(`${response.url()}: HTTP ${response.status()}`);
    }
  });

  return { consoleErrors, failedRequests, resourceEntries };
}

/** Waits until the analysis has produced a report and the editor is no longer loading. */
export async function waitForAnalysis(page: Page) {
  await page.goto('/');
  await expect(page.locator('.al-panel')).toHaveCount(4, { timeout: 30_000 });
  await expect(page.locator('.al-diagnostics')).toBeVisible();
  // The status strip says "Analysing" while the debounce is pending; wait for the
  // diagnostics list to settle instead of sleeping an arbitrary amount of time.
  await expect(page.locator('.al-diagnostic-list, .al-empty').first()).toBeVisible();
}

/** Reads the resource-timing host set from inside the page. */
export async function resourceHosts(page: Page): Promise<string[]> {
  return page.evaluate(() =>
    performance
      .getEntriesByType('resource')
      .map((entry) => new URL(entry.name, location.href).host)
      .filter((host) => host.length > 0)
      .sort(),
  );
}
