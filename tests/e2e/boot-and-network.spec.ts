import { test, expect } from '@playwright/test';
import { watchPage, waitForAnalysis, resourceHosts } from './helpers';

/**
 * Gate 2 / A1: the app boots without a console error and every referenced
 * asset returns 200. A resource-embedding glob that silently matched nothing
 * once crashed the app on boot while build, format and all engine tests were
 * green — this is the only gate that can see it.
 */
test('boots with no console errors and all assets load', async ({ page }) => {
  const watch = watchPage(page);
  await waitForAnalysis(page);

  expect(watch.consoleErrors).toEqual([]);
  expect(watch.failedRequests).toEqual([]);

  const statuses = await page.evaluate(() =>
    performance
      .getEntriesByType('resource')
      .map((entry) => ({ name: entry.name, status: (entry as PerformanceResourceTiming).responseStatus })),
  );
  const bad = statuses.filter((entry) => typeof entry.status === 'number' && entry.status >= 400);
  expect(bad).toEqual([]);
  expect(statuses.length).toBeGreaterThan(0);
});

/**
 * Gate 2 / A4 (runtime half): ADR 0003 allows only same-origin assets. The
 * source-level LocalFirstGuardTests can prove no client was constructed; only
 * the browser can prove no request left the page during an analysis run.
 */
test('every resource request stays on the same origin', async ({ page }) => {
  const requests: string[] = [];
  page.on('request', (request) => requests.push(request.url()));
  await waitForAnalysis(page);

  const hosts = await resourceHosts(page);
  const own = await page.evaluate(() => location.host);
  const initialRequestCount = requests.length;

  expect(hosts.length).toBeGreaterThan(0);
  expect([...new Set(hosts)]).toEqual([own]);

  const status = page.locator('.al-statusbar');
  const editor = page.locator('.cm-content[aria-label="Workflow YAML"]');
  await editor.click();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.type('X');
  await expect(status).toContainText('Analysing');
  await expect(status).not.toContainText('Analysing');

  const select = page.locator('#al-example');
  const options = await select.locator('option').evaluateAll((nodes) =>
    nodes.map((node) => (node as HTMLOptionElement).value),
  );
  await select.selectOption(options[1]);
  await expect(select).toHaveValue(options[1]);
  await expect(status).not.toContainText('Analysing');

  expect(requests.slice(initialRequestCount)).toEqual([]);
  expect(requests.filter((url) => new URL(url).host !== own)).toEqual([]);
});

/**
 * Gate 2 / branch C item 2: late JS->.NET callbacks must not become unhandled
 * promise rejections, and switching documents repeatedly must not leave orphan
 * CodeMirror views (each holds a ResizeObserver) behind.
 */
test('repeated document swaps leave no console errors or orphan editors', async ({ page }) => {
  const watch = watchPage(page);
  await waitForAnalysis(page);

  const select = page.locator('#al-example');
  const options = await select.locator('option').evaluateAll((nodes) =>
    nodes.map((node) => (node as HTMLOptionElement).value),
  );
  expect(options.length).toBeGreaterThan(1);

  // Type first so a debounced callback is in flight when the document changes,
  // then swap twice more while .NET and JS are both busy.
  const editor = page.locator('.cm-content').first();
  await expect(editor).toBeVisible({ timeout: 20_000 });
  await editor.click();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.type('X', { delay: 0 });
  await select.selectOption(options[1]);
  await select.selectOption(options[2]);
  await select.selectOption(options[0]);
  await page.waitForTimeout(800);

  expect(watch.consoleErrors).toEqual([]);
  expect(watch.failedRequests).toEqual([]);
  // attach() calls dispose() first, so there is exactly one live view per host.
  expect(await page.locator('.cm-editor').count()).toBe(1);
});
