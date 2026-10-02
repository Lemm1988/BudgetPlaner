// Release-Build: App-Shell aus dem Service-Worker-Cache, Daten aus IndexedDB – Neustart ohne Netz.
const { chromium } = require('@playwright/test');
const BASE = process.env.BASE || 'http://localhost:5300';
(async () => {
  const browser = await chromium.launch();
  const context = await browser.newContext({ locale: 'de-DE', viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  const t0 = Date.now();
  await page.goto(BASE + '/', { waitUntil: 'load' });
  await page.waitForSelector('.boot', { state: 'detached', timeout: 30000 });
  console.log('Erster Start (kalt) bis interaktiv:', Date.now() - t0, 'ms');
  const email = `pwa-${Date.now()}@example.com`;
  await page.goto(BASE + '/login');
  await page.fill('#email', email);
  await page.click('form button[type=submit]');
  await page.waitForSelector('#code');
  let code = null;
  for (let i = 0; i < 40 && !code; i++) {
    const r = await page.request.get(`${BASE}/api/v1/dev/latest-mail?to=${encodeURIComponent(email)}`);
    if (r.ok()) { const m = await r.json(); const c = m.text.match(/(?<![\w-])\d{6}(?![\w-])/g); if (c) code = c.pop(); }
    if (!code) await page.waitForTimeout(250);
  }
  await page.fill('#code', code);
  await page.click('form button[type=submit]');
  await page.waitForURL(/uebersicht/);
  const token = await page.evaluate(() => JSON.parse(localStorage.getItem('bp.session')).accessToken);
  await page.request.post(BASE + '/api/v1/demo', { headers: { Authorization: 'Bearer ' + token } });
  await page.goto(BASE + '/budget', { waitUntil: 'networkidle' });
  await page.waitForTimeout(2500);
  const swState = await page.evaluate(async () => {
    const reg = await navigator.serviceWorker.getRegistration();
    const keys = await caches.keys();
    let entries = 0;
    for (const k of keys) entries += (await (await caches.open(k)).keys()).length;
    return { active: !!reg?.active, controller: !!navigator.serviceWorker.controller, caches: keys, entries };
  });
  console.log('Service Worker:', JSON.stringify(swState));
  // Warmer Start
  const t1 = Date.now();
  await page.reload({ waitUntil: 'load' });
  await page.waitForSelector('.boot', { state: 'detached', timeout: 30000 });
  await page.getByText('Gehalt Alex').first().waitFor({ timeout: 15000 });
  console.log('Warmer Start bis Budget sichtbar:', Date.now() - t1, 'ms');

  // Offline neu laden
  await context.setOffline(true);
  const t2 = Date.now();
  await page.reload({ waitUntil: 'load' });
  await page.waitForSelector('.boot', { state: 'detached', timeout: 30000 });
  const visible = await page.getByText('Gehalt Alex').first().isVisible().catch(() => false);
  await page.waitForTimeout(1500);
  const banner = await page.getByText(/Du bist offline/).isVisible().catch(() => false);
  console.log('Offline-Neustart:', Date.now() - t2, 'ms · Daten sichtbar:', visible, '· Offline-Hinweis:', banner);
  if (process.env.OUT) await page.screenshot({ path: process.env.OUT + '/offline-release.png' });
  console.log('Fehler:', errors.length ? errors.join(' | ') : '(keine)');
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
