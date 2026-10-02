import { expect, test } from '@playwright/test';
import { expectNoHorizontalScroll, loadDemo, login, skipOnboarding } from './helpers';

// F-090: Tab-Navigation unter 768 px, alle Module 1 Tap entfernt, Kopfleiste bleibt sichtbar.
// F-092: Touch-Ziele ≥ 44×44 px, Eingabefelder ≥ 16 px (kein Auto-Zoom).
test('Mobile Tab-Leiste und Touch-Ziele', async ({ page }) => {
  await login(page);
  await skipOnboarding(page);
  await loadDemo(page);

  const tabbar = page.getByRole('navigation', { name: 'Hauptnavigation' }).last();
  const tabs = [
    ['Übersicht', /\/uebersicht/],
    ['Budget', /\/budget/],
    ['Schulden', /\/schulden/],
    ['Cashflow', /\/cashflow/],
    ['Polster', /\/polster/],
  ] as const;
  for (const [name, url] of tabs) {
    await tabbar.getByRole('link', { name, exact: true }).tap();
    await expect(page).toHaveURL(url);
    await expect(page.locator('.app-header')).toBeInViewport();
    await expectNoHorizontalScroll(page);
  }

  // Touch-Ziele der Tab-Leiste
  for (const box of await tabbar.getByRole('link').evaluateAll(els => els.map(e => e.getBoundingClientRect()))) {
    expect(box.width).toBeGreaterThanOrEqual(44);
    expect(box.height).toBeGreaterThanOrEqual(44);
  }

  // Kein Auto-Zoom: Schriftgröße der Eingabefelder ≥ 16 px
  await page.goto('/budget');
  await page.locator('.fab').tap();
  const dialog = page.getByRole('dialog', { name: 'Neue Position' });
  await expect(dialog).toBeVisible();
  const sizes = await dialog.locator('input:not([type=radio]):not([type=checkbox]), select, textarea')
    .evaluateAll(els => els.map(e => parseFloat(getComputedStyle(e).fontSize)));
  for (const s of sizes) expect(s).toBeGreaterThanOrEqual(16);
});
