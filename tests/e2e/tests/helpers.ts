import { expect, type APIRequestContext, type Page } from '@playwright/test';

/** Eindeutige Test-E-Mail (jeder Test bekommt einen eigenen Haushalt). */
export function uniqueEmail(prefix = 'e2e'): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1e6)}@example.com`;
}

/** Letzten 6-stelligen Code aus dem Entwicklungs-Postfach lesen. */
export async function latestCode(request: APIRequestContext, email: string): Promise<string> {
  for (let i = 0; i < 80; i++) {
    const r = await request.get(`/api/v1/dev/latest-mail?to=${encodeURIComponent(email)}`);
    if (r.ok()) {
      const mail = await r.json();
      const codes = String(mail.text).match(/(?<![\w-])\d{6}(?![\w-])/g);
      if (codes) return codes[codes.length - 1];
    }
    await new Promise(res => setTimeout(res, 250));
  }
  throw new Error(`Keine Anmelde-Mail für ${email}`);
}

/** Anmeldung per E-Mail-Code (F-001); neue Adresse = neues Konto mit eigenem Haushalt. */
export async function login(page: Page, email = uniqueEmail()): Promise<string> {
  await page.goto('/login');
  await page.locator('#email').fill(email);
  await page.locator('form button[type=submit]').click();
  await page.locator('#code').waitFor();
  await page.locator('#code').fill(await latestCode(page.request, email));
  await page.locator('form button[type=submit]').click();
  await page.waitForURL(/\/uebersicht/);
  return email;
}

/** Einrichtungsassistent schließen, falls er erscheint (neuer, leerer Haushalt). */
export async function skipOnboarding(page: Page): Promise<void> {
  const wizard = page.getByRole('dialog', { name: 'Willkommen bei BudgetPlaner!' });
  try {
    await wizard.waitFor({ state: 'visible', timeout: 8_000 });
    await wizard.getByRole('button', { name: 'Überspringen' }).click();
    await wizard.waitFor({ state: 'detached' });
  } catch {
    /* kein Assistent */
  }
}

export async function accessToken(page: Page): Promise<string> {
  return page.evaluate(() => JSON.parse(localStorage.getItem('bp.session') ?? '{}').accessToken as string);
}

export async function api(page: Page, method: 'GET' | 'POST' | 'DELETE', url: string, data?: unknown) {
  const token = await accessToken(page);
  return page.request.fetch(url, { method, data, headers: { Authorization: `Bearer ${token}` } });
}

/** Beispieldaten laden (F-142) und warten, bis der Client sie synchronisiert hat. */
export async function loadDemo(page: Page): Promise<void> {
  const r = await api(page, 'POST', '/api/v1/demo');
  expect(r.status()).toBe(204);
  await page.reload();
  await expect(page.locator('.kpi-bar')).toContainText(/5\.\d{3},\d{2}/, { timeout: 20_000 });
}

/** Kein horizontales Scrollen der Seite (F-121, responsive 320–2560 px). */
export async function expectNoHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, `horizontaler Überlauf auf ${page.url()}`).toBeLessThanOrEqual(0);
}

/** Wert einer Kennzahl in der Statusleiste, z. B. „Unverplant“. */
export function kpi(page: Page, label: string) {
  return page.locator('.kpi', { has: page.locator('.kpi-label', { hasText: label }) }).locator('.kpi-value');
}

/** Betragsfeld ausfüllen und verlassen (Übernahme beim Verlassen des Feldes). Pflichtfelder tragen ein „*“ im Label. */
export async function fillAmount(page: Page, label: string | RegExp, value: string, scope?: ReturnType<Page['locator']>): Promise<void> {
  const pattern = typeof label === 'string' ? new RegExp('^' + label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '(\\s*\\*)?$') : label;
  const field = (scope ?? page.locator('body')).getByLabel(pattern).first();
  await field.fill(value);
  await field.press('Tab');
}
