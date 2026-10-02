import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { expectNoHorizontalScroll, loadDemo, login, skipOnboarding } from './helpers';

const appPages = ['/uebersicht', '/budget', '/schulden', '/cashflow', '/polster', '/statistik', '/prognosen', '/profil', '/haushalt', '/hilfe', '/daten'];
const publicPages = ['/', '/login', '/rechner', '/rechner/zinseszins', '/rechner/tilgung', '/glossar', '/datenschutz'];

// F-121 / NFR: kein horizontales Scrollen bei 200 % Zoom (≙ 640 px CSS-Breite bei 1280 px) und ab 320 px Breite.
test('Kein horizontales Scrollen bei 200 % Zoom und 320 px', async ({ page }) => {
  await login(page);
  await skipOnboarding(page);
  await loadDemo(page);
  for (const width of [640, 320]) {
    await page.setViewportSize({ width, height: 720 });
    for (const path of appPages) {
      await page.goto(path);
      await page.waitForLoadState('networkidle');
      await expectNoHorizontalScroll(page);
    }
  }
});

// WCAG 2.1 AA: keine schwerwiegenden axe-Verstöße (hell und dunkel).
for (const scheme of ['light', 'dark'] as const) {
  test(`Barrierefreiheit (axe, ${scheme})`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    const violations: string[] = [];
    const check = async (path: string) => {
      await page.goto(path);
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(300);
      const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
      for (const v of result.violations.filter(v => v.impact === 'serious' || v.impact === 'critical'))
        violations.push(`${path}: [${v.id}] ${v.help} → ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(' | ')}`);
    };
    for (const path of publicPages) await check(path);
    await login(page);
    await skipOnboarding(page);
    await loadDemo(page);
    for (const path of appPages) await check(path);
    expect(violations, violations.join('\n')).toEqual([]);
  });
}

// B-10: öffentliche Rechner ohne Anmeldung, Ergebnis sofort.
test('Finanzrechner ohne Anmeldung', async ({ page }) => {
  await page.goto('/rechner/zinseszins');
  await expect(page.getByText('95.769,94 €')).toBeVisible();
  await page.goto('/rechner/kredit');
  await expect(page.locator('.calc-result')).toContainText('€');
  await page.goto('/rechner/stundenlohn');
  await expect(page.locator('.calc-result')).toContainText('14,42 €');
});
