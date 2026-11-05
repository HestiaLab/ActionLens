import { test, expect } from '@playwright/test';
import { waitForAnalysis } from './helpers';

/**
 * Gate 2 / branch C item 2, second half. A late edit can arrive after .NET has
 * started disposing; the bundle must swallow that rejection instead of surfacing
 * an unhandled promise error. But swallowing must not mean *closing* the editor:
 * one transient interop hiccup used to set `held.closed = true` forever, so every
 * later keystroke was silently dropped and never reached the analysis session.
 *
 * A fake .NET target that rejects exactly once makes the difference observable:
 * two edits must produce two callback attempts, not one.
 */
test('a transient interop failure does not silence later edits', async ({ page }) => {
  await waitForAnalysis(page);

  await page.evaluate(async () => {
    const mod = await import('/js/dist/actionlens-editor.js');
    const host = document.createElement('div');
    host.id = 'al-interop-probe';
    host.style.height = '120px';
    document.body.appendChild(host);

    let calls = 0;
    (window as unknown as { __alProbeCalls: () => number }).__alProbeCalls = () => calls;
    const fake = {
      invokeMethodAsync: async (_method: string, _text: string) => {
        calls += 1;
        if (calls === 1) throw new Error('transient interop hiccup');
        return null;
      },
    };

    mod.attach(host, { doc: 'x', language: 'yaml', ariaLabel: 'interop probe' }, fake);
  });

  const probe = page.locator('#al-interop-probe .cm-content');
  await expect(probe).toBeVisible({ timeout: 10_000 });
  await probe.click();
  await page.keyboard.press('End');
  await page.keyboard.type('A', { delay: 0 });
  await page.keyboard.type('B', { delay: 0 });

  const calls = await page.evaluate(() =>
    (window as unknown as { __alProbeCalls: () => number }).__alProbeCalls());
  expect(calls, 'the second edit was dropped after one transient failure').toBe(2);

  await page.evaluate(async () => {
    const mod = await import('/js/dist/actionlens-editor.js');
    const host = document.getElementById('al-interop-probe')!;
    mod.dispose(host);
    host.remove();
  });
});
