import { expect, test } from '@playwright/test';
import { api, fillAmount, login, skipOnboarding } from './helpers';

// F-120: Ausgabe komplett per Tastatur anlegbar.
test('Ausgabe nur mit der Tastatur anlegen', async ({ page }) => {
  await login(page);
  await skipOnboarding(page);
  await page.goto('/budget');
  await expect(page.getByRole('heading', { name: 'Budget', exact: true })).toBeVisible();

  // Mit Tab bis „Neue Position“
  let found = false;
  for (let i = 0; i < 80 && !found; i++) {
    await page.keyboard.press('Tab');
    found = await page.evaluate(() => document.activeElement?.textContent?.trim() === 'Neue Position');
  }
  expect(found, '„Neue Position“ per Tab erreichbar').toBe(true);
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Neue Position' });
  await expect(dialog).toBeVisible();

  // Fokus liegt im Dialog (Fokusfalle); weiter bis zum Namensfeld
  let inName = false;
  for (let i = 0; i < 20 && !inName; i++) {
    inName = await page.evaluate(() => document.activeElement?.id === 'entry-name');
    if (!inName) await page.keyboard.press('Tab');
  }
  expect(inName).toBe(true);
  await page.keyboard.type('Tastatur-Test');
  await page.keyboard.press('Tab');
  await page.keyboard.type('42,50');
  await page.keyboard.press('Enter');

  await expect(dialog).toBeHidden();
  await expect(page.getByText('Tastatur-Test', { exact: true })).toBeVisible();

  // Esc schließt Dialoge
  await page.getByRole('button', { name: 'Neue Position' }).click();
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
});

// F-132: Offline-Eintrag wird spätestens 30 s nach Wiederverbindung synchronisiert; Status-Hinweis während Offline-Phase.
test('Offline anlegen, online synchronisieren', async ({ page, context }) => {
  await login(page);
  await skipOnboarding(page);
  await page.goto('/budget');
  await expect(page.getByRole('heading', { name: 'Budget', exact: true })).toBeVisible();

  await context.setOffline(true);
  await expect(page.getByText(/Du bist offline/)).toBeVisible();

  await page.getByRole('button', { name: 'Neue Position' }).click();
  const dialog = page.getByRole('dialog', { name: 'Neue Position' });
  await dialog.locator('#entry-name').fill('Offline-Eintrag');
  await fillAmount(page, 'Betrag', '19,99', dialog);
  await dialog.getByRole('button', { name: 'Speichern' }).click();
  await expect(page.getByText('Offline-Eintrag', { exact: true })).toBeVisible();

  await context.setOffline(false);
  const started = Date.now();
  await expect.poll(async () => {
    const r = await api(page, 'GET', '/api/v1/entries');
    if (!r.ok()) return false;
    const list = await r.json();
    return (list as Array<{ name: string }>).some(e => e.name === 'Offline-Eintrag');
  }, { timeout: 30_000, intervals: [500, 1000] }).toBe(true);
  expect(Date.now() - started).toBeLessThan(30_000);
  await expect(page.getByText(/Du bist offline/)).toBeHidden();
});

// F-131: Änderung auf Gerät A erscheint auf Gerät B in < 5 s.
test('Echtzeit-Synchronisation zwischen zwei Geräten', async ({ page, browser }) => {
  await login(page);
  await skipOnboarding(page);
  await page.goto('/budget');

  // Gerät B: zweiter Browser-Kontext mit derselben Anmeldung
  const state = await page.context().storageState();
  const deviceB = await browser.newContext({ storageState: state, locale: 'de-DE', timezoneId: 'Europe/Berlin' });
  const pageB = await deviceB.newPage();
  await pageB.goto('/budget');
  await expect(pageB.getByRole('heading', { name: 'Budget', exact: true })).toBeVisible();
  await pageB.waitForTimeout(1500); // SignalR verbunden

  await page.getByRole('button', { name: 'Neue Position' }).click();
  const dialog = page.getByRole('dialog', { name: 'Neue Position' });
  await dialog.locator('#entry-name').fill('Von Gerät A');
  await fillAmount(page, 'Betrag', '12', dialog);
  const saved = Date.now();
  await dialog.getByRole('button', { name: 'Speichern' }).click();

  await expect(pageB.getByText('Von Gerät A', { exact: true })).toBeVisible({ timeout: 5_000 });
  expect(Date.now() - saved).toBeLessThan(5_000);
  await deviceB.close();
});
