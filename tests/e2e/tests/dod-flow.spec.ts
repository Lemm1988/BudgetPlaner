import { expect, test } from '@playwright/test';
import { fillAmount, kpi, login } from './helpers';

// DoD: „Haushalt anlegen → Budget füllen → Unverplant zuweisen → Monat abschließen → Statistik/Prognose prüfen“ fehlerfrei.
test('DoD-Gesamtdurchlauf', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });

  // 1) Haushalt anlegen: Anmeldung legt den Haushalt an, der Einrichtungsassistent führt durch (F-140)
  await login(page);
  const wizard = page.getByRole('dialog', { name: 'Willkommen bei BudgetPlaner!' });
  await expect(wizard).toBeVisible({ timeout: 20_000 });
  await wizard.getByRole('button', { name: /Los geht/ }).click();

  await fillAmount(page, 'Betrag', '3000', wizard);
  await wizard.getByRole('button', { name: 'Weiter', exact: true }).click();

  await fillAmount(page, 'Miete / Wohnkosten', '1000', wizard);
  await fillAmount(page, 'Lebensmittel', '500', wizard);
  await wizard.getByRole('button', { name: 'Weiter', exact: true }).click();

  await fillAmount(page, 'Monatlich zurücklegen', '300', wizard);
  await wizard.getByRole('button', { name: 'Weiter', exact: true }).click();

  await expect(wizard).toContainText('1.200,00 €');
  await wizard.getByRole('button', { name: 'Budget anlegen' }).click();
  await page.waitForURL(/\/budget/);

  // 2) Budget gefüllt: Einnahmen 3.000 €, Unverplant 1.200 €
  await expect(kpi(page, 'Einnahmen')).toHaveText('3.000,00 €');
  await expect(kpi(page, 'Unverplant')).toHaveText('1.200,00 €');

  // 3) Unverplant zuweisen: neue Position über den Rest → Punktlandung (B-02)
  await page.getByRole('button', { name: 'Neue Position' }).click();
  const dialog = page.getByRole('dialog', { name: 'Neue Position' });
  await dialog.locator('#entry-name').fill('Freizeit & Familie');
  await fillAmount(page, 'Betrag', '1200', dialog);
  await dialog.getByRole('button', { name: 'Speichern' }).click();
  await expect(dialog).toBeHidden();
  await expect(kpi(page, 'Unverplant')).toHaveText('0,00 €');
  await expect(page.getByText('Punktlandung: Jeder Euro hat eine Aufgabe.')).toBeVisible();

  // 4) Monat abschließen (I-01)
  await page.getByRole('button', { name: 'Mehr' }).click();
  await page.getByRole('menuitem', { name: 'Monat abschließen' }).click();
  const close = page.getByRole('dialog', { name: /abschließen/ });
  await close.getByRole('button', { name: 'Abschließen' }).click();
  await expect(page.getByText(/Abgeschlossen am/)).toBeVisible();

  // 5a) Statistik prüfen: Segmentsumme = Gesamtausgaben (F-060), 50/30/20 summiert zu 100 % (F-062)
  await page.goto('/statistik?ansicht=expenses');
  const legend = page.locator('.legend-table');
  await expect(legend).toContainText('Bedarf');
  await expect(legend.locator('tfoot')).toContainText('2.700');
  await page.getByRole('tab', { name: '50/30/20' }).click();
  await page.getByRole('button', { name: 'Monat', exact: true }).click();
  await expect(page.locator('table tfoot')).toContainText('100,0 %');

  // 5b) Prognose prüfen: Kontostand-Prognose und Was-wäre-wenn ohne reale Änderung (F-070, F-075)
  await page.goto('/prognosen');
  await expect(page.getByRole('heading', { name: 'Kontostand-Prognose' })).toBeVisible();
  await expect(page.locator('section[aria-labelledby=fc-balance] > .table-wrap tbody tr')).toHaveCount(7);
  const scenario = page.locator('#szenario');
  await scenario.getByLabel('Änderung in %').fill('10');
  await scenario.getByLabel('Änderung in %').press('Tab');
  await scenario.getByRole('button', { name: 'Hinzufügen' }).click();
  await expect(scenario.getByText('Simulation aktiv')).toBeVisible();
  await expect(scenario.locator('table').first()).toContainText('Ø Überschuss pro Monat');
  await scenario.getByRole('button', { name: 'Verwerfen' }).click();
  await expect(scenario.getByText('Simulation aktiv')).toBeHidden();

  // Echte Daten unverändert
  await page.goto('/uebersicht');
  await expect(kpi(page, 'Einnahmen')).toHaveText('3.000,00 €');

  expect(errors, errors.join('\n')).toEqual([]);
});
