import { test, expect, type Page } from '@playwright/test';
import { waitForAnalysis } from './helpers';

async function cursorLine(page: Page, label: string): Promise<number | null> {
  return page.evaluate(async (editorLabel) => {
    const modulePath = '/js/dist/actionlens-editor.js';
    const editor = await import(modulePath);
    const content = document.querySelector<HTMLElement>(`.cm-content[aria-label="${editorLabel}"]`);
    const host = content?.closest('.cm-editor')?.parentElement;
    return host === null || host === undefined ? null : editor.getCursorLine(host);
  }, label);
}

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

  await expect(editor).toBeFocused();
  await expect.poll(() => cursorLine(page, 'Workflow YAML')).toBe(expectedLine);
});

test('a payload diagnostic opens the payload editor at the JSON error line', async ({ page }) => {
  await waitForAnalysis(page);
  await page.getByRole('tab', { name: 'Event payload' }).click();

  const payload = page.locator('.cm-content[aria-label="Event payload JSON"]');
  await expect(payload).toBeVisible({ timeout: 20_000 });
  await payload.fill('{\n  "event_name": }');

  const finding = page.locator('.al-diagnostic').filter({ hasText: 'event payload is not readable JSON' }).first();
  await expect(finding).toBeVisible({ timeout: 20_000 });
  await finding.click();

  await expect(payload).toBeFocused();
  await expect.poll(() => cursorLine(page, 'Event payload JSON')).toBe(2);
});
