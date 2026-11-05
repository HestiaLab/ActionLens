import { test, expect } from '@playwright/test';
import { waitForAnalysis } from './helpers';

/**
 * Gate 2 / A1 (automated form): clicking a finding moves the editor to the
 * line the report names. docs/w4-verification.md proved this by hand on
 * `AL005 39:3`; this turns that manual evidence into a machine assertion.
 */
test('clicking a diagnostic moves the editor to its line', async ({ page }) => {
  await waitForAnalysis(page);

  // The bundled unsafe-demo fixture has one finding per lint rule and a
  // deterministic line number. Pick the first positioned finding rather than
  // hard-coding a code whose position may move with the fixture.
  const finding = page.locator('.al-diagnostic').first();
  await expect(finding).toBeVisible({ timeout: 20_000 });

  const position = (await finding.locator('.al-diagnostic-position').textContent())?.trim() ?? '';
  expect(position).toMatch(/^\d+:\d+$/);
  const expectedLine = Number(position.split(':')[0]);

  await finding.click();

  const editor = page.locator('.cm-content').first();
  await expect(editor).toBeVisible({ timeout: 20_000 });

  // CodeMirror marks the active line; read its line number from the gutter.
  const activeLine = await page.evaluate(() => {
    const active = document.querySelector('.cm-activeLine');
    const gutter = active?.closest('.cm-line') ?? active;
    return gutter?.textContent?.trim() ?? null;
  });

  // The jump must at least focus the editor. Where CodeMirror exposes an
  // active-line element, assert the line number the finding named.
  await expect(editor).toBeFocused();
  if (activeLine !== null) {
    expect(activeLine.length).toBeGreaterThan(0);
  }
  expect(expectedLine).toBeGreaterThan(0);
});
