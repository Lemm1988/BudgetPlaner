import { defineConfig, devices } from '@playwright/test';
import os from 'node:os';
import path from 'node:path';

// Eigener Testserver mit leerer Datenbank (temporäres Datenverzeichnis), damit Entwicklungsdaten unberührt bleiben.
// Voraussetzung: `dotnet build` wurde ausgeführt (der Server startet mit --no-build).
const PORT = Number(process.env.E2E_PORT ?? 5299);
const BASE = `http://localhost:${PORT}`;
const DATA = process.env.E2E_DATA ?? path.join(os.tmpdir(), `budgetplaner-e2e-${Date.now()}`);

export default defineConfig({
  testDir: './tests',
  timeout: 120_000,
  expect: { timeout: 15_000 },
  fullyParallel: true,
  workers: process.env.CI ? 2 : 3,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'report' }]],
  use: {
    baseURL: BASE,
    locale: 'de-DE',
    timezoneId: 'Europe/Berlin',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } }, testIgnore: /mobile\.spec/ },
    { name: 'mobile', use: { ...devices['Pixel 7'] }, testMatch: /mobile\.spec/ },
  ],
  webServer: {
    command: `dotnet run --project ../../src/BudgetPlaner.Server --no-build --no-launch-profile --urls ${BASE}`,
    url: `${BASE}/health`,
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      App__DataDirectory: DATA,
      App__DevMailbox: 'true',
      App__RequireHttps: 'false',
      RateLimits__AuthPerMinute: '10000',
      RateLimits__ApiPerMinute: '100000',
    },
  },
});
