import { test, expect } from '@playwright/test';
import { watchPage, waitForAnalysis } from './helpers';

/**
 * Regression for the debounce echo race (docs/review-v1-grill.md, branch C item 1).
 *
 * Reproduced in Chromium before the fix: typing `MARK` at the start of the
 * document produced `KRAM` — every keystroke was mistaken for an external
 * value, pushed back through `setText`, and the caret was reset to offset 0 —
 * and a single undo erased all four characters.
 *
 * The assertion deliberately types at the *start* of the document. Typing at
 * the end hides the bug, because a whole-document replacement happens to leave
 * the caret where the typing was going anyway.
 */
test('typing at the start keeps character order, caret and undo', async ({ page }) => {
  await waitForAnalysis(page);

  const editor = page.locator('.cm-content').first();
  await expect(editor).toBeVisible({ timeout: 20_000 });
  expect(await page.locator('.al-editor-textarea').count()).toBe(0);

  const original = (await editor.textContent()) ?? '';
  await editor.click();
  await page.keyboard.press('ControlOrMeta+Home');

  await page.keyboard.type('MARK', { delay: 0 });

  // Cross the 250 ms debounce and the re-render it triggers. Under the old
  // implementation the echo arrived during this window.
  await page.waitForTimeout(600);

  const afterType = (await editor.textContent()) ?? '';
  expect(afterType.startsWith('MARK'), `character order was scrambled: ${afterType.slice(0, 20)}`).toBe(true);
  expect(afterType).toBe('MARK' + original);

  // The caret must still be after the four characters we typed, not back at 0.
  const caret = await page.evaluate(() => document.getSelection()?.anchorOffset ?? -1);
  expect(caret).toBe(4);

  // Undo must not destroy the original document. Under the old implementation
  // the whole-document replacements left nothing to undo back to.
  await page.keyboard.press('ControlOrMeta+z');
  const afterUndo = (await editor.textContent()) ?? '';
  expect(afterUndo.length).toBeGreaterThanOrEqual(original.length);
});

/**
 * The other half of the fix: an external replacement (loading a sample) must
 * still reach the editor. Suppressing the echo must not become "never push".
 */
test('loading a different example still replaces the document', async ({ page }) => {
  await waitForAnalysis(page);

  const editor = page.locator('.cm-content').first();
  await expect(editor).toBeVisible({ timeout: 20_000 });

  const select = page.locator('#al-example');
  const current = await select.inputValue();
  const options = await select.locator('option').evaluateAll((nodes) =>
    nodes.map((node) => (node as HTMLOptionElement).value),
  );
  const other = options.find((value) => value !== current);
  expect(other).toBeDefined();

  await select.selectOption(other!);

  // CodeMirror renders only the visible viewport, so textContent is a window
  // onto the document, not the whole file. Both bundled examples start with
  // the same 77-dash banner; the file name on the next line is what differs.
  await expect
    .poll(async () => (await editor.textContent()) ?? '', { timeout: 10_000 })
    .toContain(`${other} —`);
});

/**
 * A complementary browser guard for the shell's Session.Changed subscription
 * (docs/review-v1-grill.md §13.2). Mark the live CodeMirror host with a JS
 * property that a re-created DOM element cannot inherit, then type across
 * several debounce windows: the editor must not be unmounted and recreated
 * while the user is typing.
 *
 * Scope note: this does NOT count re-renders. Blazor reuses DOM nodes, and an
 * A/B check confirmed this test still passes with an unconditional
 * StateHasChanged. Render-count narrowing is asserted by
 * ActionLens.Ui.Tests/WorkspaceRenderTests instead. What this test adds is the
 * browser-level consequence the bUnit stub cannot see: no editor remount, no
 * lost text, no console errors.
 */
test('typing does not remount the editor host on every session change', async ({ page }) => {
  const watch = watchPage(page);
  await waitForAnalysis(page);

  await page.evaluate(() => {
    const host = document.querySelector('.cm-editor');
    if (!host) throw new Error('No CodeMirror host at startup');
    (host as HTMLElement & { __alIdentity?: string }).__alIdentity = 'kept';
  });

  const editor = page.locator('.cm-content').first();
  await expect(editor).toBeVisible({ timeout: 20_000 });
  await editor.click();
  await page.keyboard.press('ControlOrMeta+Home');

  // Several short bursts, each followed by a wait longer than the 250 ms debounce.
  for (const chunk of ['AAA', 'BBB', 'CCC']) {
    await page.keyboard.type(chunk, { delay: 0 });
    await page.waitForTimeout(450);
  }

  const identity = await page.evaluate(() =>
    (document.querySelector('.cm-editor') as HTMLElement & { __alIdentity?: string } | null)?.__alIdentity ?? null);
  expect(identity, 'the CodeMirror host was replaced during typing').toBe('kept');

  const text = (await editor.textContent()) ?? '';
  expect(text.startsWith('AAABBBCCC'), `unexpected document head: ${text.slice(0, 20)}`).toBe(true);
  expect(watch.consoleErrors).toEqual([]);
});
