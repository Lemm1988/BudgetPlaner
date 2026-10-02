import { expect, test, type Page } from '@playwright/test';
import { fillAmount, login, skipOnboarding } from './helpers';

async function addEntry(page: Page, name: string, amount: string) {
  await page.getByRole('button', { name: 'Neue Position' }).click();
  const dialog = page.getByRole('dialog', { name: 'Neue Position' });
  await dialog.locator('#entry-name').fill(name);
  await fillAmount(page, 'Betrag', amount, dialog);
  await dialog.getByRole('button', { name: 'Speichern' }).click();
  await expect(dialog).toBeHidden();
}

async function order(page: Page, names: string[]): Promise<string[]> {
  const all = await page.locator('.entry-name').allInnerTexts();
  return all.map(t => t.trim()).filter(t => names.includes(t));
}

// F-015: Sortiermodus – per Drag & Drop (Maus/Touch) und barrierefrei per Pfeil-Schaltflächen; Reihenfolge bleibt nach dem Neuladen.
test('Sortiermodus: Tasten und Drag & Drop, Reihenfolge bleibt erhalten', async ({ page }) => {
  await login(page);
  await skipOnboarding(page);
  await page.goto('/budget');
  const names = ['Sort A', 'Sort B', 'Sort C'];
  for (const n of names) await addEntry(page, n, '10');
  await expect.poll(() => order(page, names)).toEqual(names);

  await page.getByRole('button', { name: 'Sortieren', exact: true }).click();

  // Barrierefreie Variante: „Sort C“ nach oben
  const rowC = page.locator('.entry', { has: page.locator('.entry-name', { hasText: 'Sort C' }) });
  await rowC.getByRole('button', { name: 'Nach oben' }).click();
  await expect.poll(() => order(page, names)).toEqual(['Sort A', 'Sort C', 'Sort B']);

  // Drag & Drop: „Sort A“ unter „Sort B“ ziehen
  const handle = page.getByRole('button', { name: '„Sort A“ verschieben' });
  const target = page.locator('.entry', { has: page.locator('.entry-name', { hasText: 'Sort B' }) });
  const from = (await handle.boundingBox())!;
  const to = (await target.boundingBox())!;
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  for (let i = 1; i <= 10; i++) await page.mouse.move(from.x + from.width / 2, from.y + (to.y + to.height * 0.8 - from.y) * i / 10);
  await page.mouse.up();
  await expect.poll(() => order(page, names)).toEqual(['Sort C', 'Sort B', 'Sort A']);

  await page.getByRole('button', { name: 'Sortieren beenden' }).click();
  await page.reload();
  await expect.poll(() => order(page, names), { timeout: 15_000 }).toEqual(['Sort C', 'Sort B', 'Sort A']);
});
