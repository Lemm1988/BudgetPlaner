# BudgetPlaner – Projektstatus

> Arbeitsübersicht. Wird nach jedem Arbeitsschritt aktualisiert.
> Fachregeln: [ENTSCHEIDUNGEN.md](ENTSCHEIDUNGEN.md) · Nachweis je Anforderung: [ANFORDERUNGEN.md](ANFORDERUNGEN.md)

## Ziel
Blazor-PWA (C#/.NET 10) mit vollem Funktionsumfang von BudgetHeld laut `Lastenheft.docx` (F-001–F-142 + NFR)
plus Original-Funktionen und Verbesserungen.

## Architektur (Clean Architecture)

```
src/
  BudgetPlaner.Domain          Entitäten, Enums, Perioden-/Rechenlogik (Budget, Cashflow, Schulden,
                               Polster, Statistik, Prognosen, Ampel, Szenario) – keine Abhängigkeiten
  BudgetPlaner.Shared          API-Verträge (DTOs), JSON-Kontext, Übersetzungen DE/EN, Glossar
  BudgetPlaner.Application     Use-Cases/Services, Schnittstellen (DB, Mail, Push, Uhr), Sync-Merge
  BudgetPlaner.Infrastructure  EF Core + SQLite, Verschlüsselung, SMTP/Dev-Postfach, WebPush, Excel/CSV,
                               Passkeys (Fido2), Hintergrunddienste
  BudgetPlaner.Server          ASP.NET Core: REST-API /api/v1 (JWT), SignalR-Hub, OpenAPI/Swagger,
                               Rate-Limiting, Hosting des WASM-Clients
  BudgetPlaner.Client          Blazor WebAssembly PWA: UI, IndexedDB-Offline-Store, Outbox-Sync, SignalR
tests/
  BudgetPlaner.Domain.Tests    Unit-Tests aller Akzeptanzkriterien mit Zahlenbeispielen
  BudgetPlaner.Server.Tests    Integrations-Tests (API, Auth, Rollen, Sync, Export/Import, Löschung)
  e2e/                         Playwright: Gesamtdurchlauf, Mobil, Offline, Echtzeit, Zoom 200 %
```

Datenfluss: Client hält eine vollständige Kopie der Haushaltsdaten (IndexedDB) und rechnet lokal mit
derselben Domain-Bibliothek wie der Server → offlinefähig. Änderungen gehen über eine Outbox an
`POST /api/v1/sync/push`; der Server merged feldweise, erhöht die Haushalts-Sequenz und meldet per SignalR;
Clients holen Deltas über `GET /api/v1/sync?since=n`.

## Phasen & Fortschritt

### Phase 0 – Setup
- [x] Lastenheft gelesen, Original recherchiert, Inkonsistenzen geklärt (D-01, D-02)
- [x] Solution, Projekte, Pakete
- [x] Übersichtsdokumente

### Phase 1 – Domain (Rechenkern)
- [x] Perioden/Monatsstart, Rhythmen, Fälligkeiten
- [x] Budget-Monat (Unverplant, Überschuss, Ist/Soll, Snapshots)
- [x] Cashflow + Kontostand
- [x] Schulden (Annuität, Tilgungsplan, Prognose, Schneeball/Lawine)
- [x] Polster (Stand, Fortschritt, Prognose)
- [x] Statistik F-060–F-068
- [x] Prognosen F-070–F-075, Szenario
- [x] Ampel-Regeln
- [x] Rechner (Zinseszins, Kredit, Stundenlohn, Ruhestand, Hypothek, Tilgung)
- [x] Unit-Tests aller AK

### Phase 2 – Server
- [x] EF Core/SQLite, Verschlüsselung, Migrationen
- [x] Auth: Magic Link + Code, JWT/Refresh, Rate-Limit, Passkeys
- [x] Haushalt, Rollen, Einladungen, Owner-Übertragung
- [x] Sync push/pull + Merge + Änderungsverlauf, SignalR
- [x] REST-Ressourcen /api/v1 + OpenAPI/Swagger + API-Tokens
- [x] Automatische Buchungen (Raten, Sparpläne)
- [x] Benachrichtigungen (In-App, Push, E-Mail) + Scheduler
- [x] Export CSV/Excel/JSON, Import mit Spaltenzuordnung
- [x] Konto-/Haushaltslöschung, Backups (14 Tage)
- [x] Integrations-Tests (25)

### Phase 3 – Client
- [x] App-Shell, Navigation (Desktop/Mobil-Tabs), Statusleiste, Theme, i18n
- [x] Offline-Store, Outbox, Sync, Online-/Offline-Status
- [x] Login/Verify/Passkey, Einladung annehmen
- [x] Übersicht/Dashboard + Ampel
- [x] Budget (Positionen, Kategorien, Sortiermodus, Rückgängig, Ist, Monatsabschluss)
- [x] Schulden (Liste, Detail, Tilgungsplan, Zahlungen, Strategievergleich), Cashflow (Kalender/Liste, Abgleich, Engpässe), Polster (Liste, Detail, Zuweisen/Entnehmen)
- [x] Statistik (6 Reiter F-060–F-068), Prognosen + Szenario-Editor (F-070–F-075)
- [x] Profil, Haushalt, Benachrichtigungen, Änderungsverlauf, Export/Import-Assistent, Kontolöschung
- [x] Onboarding-Assistent, Hilfe/Glossar, Demodaten (zweisprachig)
- [x] 6 Rechner, Landingpage, Impressum/Datenschutz (Vorlage), Versionshinweise, Teilen

### Phase 4 – PWA
- [x] Manifest, Icons, Splash, Service Worker (Offline-Cache, Push, Update-Steuerung), Installationshinweis, Web-Push

### Phase 5 – Qualität
- [x] E2E-Tests (Playwright, 10): DoD-Durchlauf, Tastatur, Offline, Echtzeit, Mobil, Sortiermodus, Zoom/320 px, axe hell/dunkel, Rechner
- [x] Release-Build geprüft (Service Worker, Offline-Neustart, Ladezeit 0,9 s)
- [x] Performance, Sicherheitsreview, DSGVO-Checkliste → [ABNAHME.md](ABNAHME.md)
- [x] README / Betriebsanleitung, Anforderungs-Nachweis vollständig

## Offene Punkte für den Auftraggeber
- Echte E-Mail-Zustellung braucht SMTP-Zugangsdaten (bis dahin: Dev-Postfach unter `/dev/postfach`).
- DoD „PWA auf Android- und iOS-Gerät getestet“ ist ein manueller Schritt (Anleitung im README).

## Log
- 2026-10-01: Start. Setup, Recherche, Entscheidungen D-01/D-02.
- 2026-10-01: Phase 1 fertig – Domain-Rechenkern, 82 Unit-Tests grün; 10.000 Positionen: Monat+24-Monats-Statistik+Cashflow ≈ 100 ms (JIT).
- 2026-10-02: Phase 2 + 3 + 4: Server, alle Seiten, PWA. Erste Browser-Prüfung (Playwright/Chromium): App bootet unter CSP;
  Fix: `UseBlazorFrameworkFiles` kollidierte mit `MapStaticAssets` (.NET 10) → entfernt. Demodaten: Ganzzahl-Überlauf bei den
  Ist-Abweichungen behoben, Demodaten DE/EN. Vorjahresvergleich nur über Monate mit Daten in beiden Jahren (I-19).
- 2026-10-02: Phase 5: E2E-Suite (10 Tests) + Release-Check. Durch die Tests gefunden und behoben: Outbox-Reihenfolge
  (abhängige Datensätze), Dialoge verloren Eingaben bei Sync, Kontraste (axe), Überlauf bei 320 px. Release-Build:
  Platzhalter in index.html wurden beim Veröffentlichen nicht ersetzt → stabile Routen + Service-Worker-Zuordnung.
  Sicherheitsreview: Dev-Postfach nur Development/Testing, Push-SSRF-Schutz, CSP-WebSocket, App_Data nicht veröffentlichen.
  Stand: 82 Domain-, 28 Server-, 10 E2E-Tests grün; 0 Build-Warnungen.
