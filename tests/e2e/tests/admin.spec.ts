import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { api, expectNoHorizontalScroll, login, skipOnboarding } from './helpers';

/** Entspricht App__AdminEmails im playwright.config.ts. */
const ADMIN = 'admin-e2e@example.com';

test('Verwaltung: nur für Admins; Konto sperren, entsperren, löschen; Registrierung einschränken', async ({ browser, page }) => {
  // Normales Konto in eigenem Browser-Kontext: keine Verwaltung.
  const userContext = await browser.newContext();
  const userPage = await userContext.newPage();
  const userEmail = await login(userPage);
  await skipOnboarding(userPage);
  await expect(userPage.locator('nav.sidebar').getByRole('link', { name: 'Verwaltung' })).toHaveCount(0);
  expect((await api(userPage, 'GET', '/api/v1/admin/overview')).status()).toBe(403);

  // Administrator: Menüpunkt, Status, Kontenliste.
  await login(page, ADMIN);
  await skipOnboarding(page);
  await page.locator('nav.sidebar').getByRole('link', { name: 'Verwaltung' }).click();
  await expect(page).toHaveURL(/\/verwaltung$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Verwaltung' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Serverstatus' })).toBeVisible();
  const row = page.locator(`li.admin-user[data-email="${userEmail}"]`);
  await expect(row).toBeVisible();
  await expect(page.locator(`li.admin-user[data-email="${ADMIN}"]`).getByRole('button')).toHaveCount(0);

  // Barrierefreiheit und 320 px ohne horizontales Scrollen.
  const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  const serious = axe.violations.filter(v => v.impact === 'serious' || v.impact === 'critical').map(v => `${v.id}: ${v.nodes.map(n => n.target.join(' ')).join(' | ')}`);
  expect(serious, serious.join('\n')).toEqual([]);
  await page.setViewportSize({ width: 320, height: 720 });
  await expectNoHorizontalScroll(page);
  await page.setViewportSize({ width: 1440, height: 900 });

  // Sperren mit Grund: Sitzung des Kontos endet sofort, neue Anmeldung wird abgewiesen.
  await row.getByRole('button', { name: `${userEmail} sperren` }).click();
  await row.getByLabel('Grund (optional, nur für Admins sichtbar)').fill('E2E-Test');
  await row.locator('form').getByRole('button', { name: 'Sperren' }).click();
  await expect(row.locator('.badge.bad')).toHaveText('Gesperrt');
  await expect(row).toContainText('E2E-Test');
  await expect(userPage).toHaveURL(/\/login/, { timeout: 20_000 });
  await userPage.locator('#email').fill(userEmail);
  await userPage.locator('form button[type=submit]').click();
  await expect(userPage.locator('.form-error')).toHaveText('Dieses Konto wurde vom Betreiber gesperrt.');

  // Entsperren.
  await row.getByRole('button', { name: `${userEmail} entsperren` }).click();
  await expect(row.locator('.badge.bad')).toHaveCount(0);

  // Registrierung auf „Nur Freigegebene“ – @example.com bleibt frei, damit parallel laufende Tests weiter Konten anlegen.
  await page.locator('label', { hasText: 'Nur Freigegebene' }).click();
  await page.getByLabel('Freigegebene Adressen und Domains').fill('@example.com');
  await page.getByRole('button', { name: 'Speichern' }).click();
  await expect(page.getByText('Registrierung gespeichert.')).toBeVisible();
  const anon = await browser.newContext();
  const anonPage = await anon.newPage();
  await anonPage.goto('/login');
  await expect(anonPage.getByText(/nur auf Einladung/)).toBeVisible();
  await anonPage.locator('#email').fill(`fremd-${Date.now()}@example.org`);
  await anonPage.locator('form button[type=submit]').click();
  await expect(anonPage.locator('.form-error')).toContainText('die Registrierung ist eingeschränkt');

  // „Auf Anfrage“: Unbekannte stellen eine Anfrage (ohne Mail), der Admin schaltet frei, danach klappt die Anmeldung.
  await page.locator('label', { hasText: 'Auf Anfrage' }).click();
  await page.getByRole('button', { name: 'Speichern' }).click();
  await expect.poll(async () => (await (await page.request.get('/api/v1/version')).json()).registrationMode).toBe('approval');
  const requester = `anfrage-${Date.now()}@example.org`;
  await anonPage.goto('/login');
  await expect(anonPage.getByText(/stellst du eine Anfrage/)).toBeVisible();
  await anonPage.locator('#email').fill(requester);
  await anonPage.locator('form button[type=submit]').click();
  await expect(anonPage.getByRole('heading', { name: 'Anfrage gesendet' })).toBeVisible();
  await page.getByRole('button', { name: 'Aktualisieren' }).click();
  const request = page.locator(`li[data-request="${requester}"]`);
  await request.getByRole('button', { name: `${requester} freischalten` }).click();
  await expect(request).toHaveCount(0);
  await expect(page.getByLabel('Freigegebene Adressen und Domains')).toHaveValue(/anfrage-\d+@example\.org/);
  await login(anonPage, requester);

  await page.locator('label', { hasText: 'Offen' }).click();
  await page.getByRole('button', { name: 'Speichern' }).click();
  await expect.poll(async () => (await (await page.request.get('/api/v1/version')).json()).registrationMode).toBe('open');

  // Löschen mit Eingabe der Adresse.
  await row.getByRole('button', { name: `${userEmail} löschen` }).click();
  const dialog = page.getByRole('dialog', { name: 'Konto endgültig löschen?' });
  await dialog.getByLabel(`Zur Bestätigung „${userEmail}“ eingeben`).fill(userEmail);
  await dialog.getByRole('button', { name: 'Löschen' }).click();
  await expect(row).toHaveCount(0);

  await anon.close();
  await userContext.close();
});
