# Anforderungs-Nachweis (Traceability)

Status: ⬜ offen · 🟨 teilweise / manueller Schritt · ✅ umgesetzt & automatisiert getestet

Testsuiten: **D** = `tests/BudgetPlaner.Domain.Tests` (82) · **S** = `tests/BudgetPlaner.Server.Tests` (28) ·
**E** = `tests/e2e` (Playwright, 10). Testnamen beginnen mit der Anforderungs-ID.

## Funktionale Anforderungen

| ID | Anforderung | Umsetzung | Nachweis/Test | Status |
|----|-------------|-----------|---------------|--------|
| F-001 | Registrierung per E-Mail (Magic Link) | `AuthService`, `/login`, `/login/verify` (Link **und** 6-stelliger Code, I-10), Passkeys (B-15) | S: „F-001: Registrierung …“, „… Rate-Limit“, „… einmal einlösbar“, „… 15 Minuten“ · E: alle Tests melden sich per Code an | ✅ |
| F-002 | Haushalt anlegen/verwalten, Einladungen | Haushalt wird bei Registrierung angelegt; `/haushalt` (Name, Einladung per Mail/Link, Mitglieder) | S: „F-002/F-003: Einladung …“, „F-003: Entfernen …“ · E: DoD-Gesamtdurchlauf | ✅ |
| F-003 | Rollen Owner/Mitglied | `UserContext.RequireOwner`, Owner-Übertragung, Entzug wirkt sofort (SignalR „revoked“) | S: „F-002/F-003 …“, „F-003: Entfernen entzieht sofort den Zugriff …“ | ✅ |
| F-010 | Einnahmen erfassen | `EntryDialog`, wiederkehrend in Folgemonate | D: „F-010: Monatliche Einnahmen …“, „F-010: Löschen …“ · E: DoD | ✅ |
| F-011 | Ausgaben erfassen (fix/variabel, pausieren) | Fix/variabel, Pause ab Monat / einmalig aussetzen | D: „F-011: Pausieren …“ · E: DoD, Tastatur | ✅ |
| F-012 | Kategorien (50/30/20, Unterkategorien, Sonstiges) | D-01: Bedarf/Wunsch/Sparen + Bereiche; `CategoryManager` | S: „F-012: Löschen einer Kategorie …“ | ✅ |
| F-013 | Rhythmus & Abbuchungstag | `EntrySchedule` (monatlich … jährlich, einmalig, Ende) | D: „F-013: …“ (3), „Rhythmus vierteljährlich …“ | ✅ |
| F-014 | Unverplant-Berechnung | `BudgetCalculator`, Zielzone (B-02) | D: „F-014: …“ (4) · S: „F-014 über die API …“ · E: DoD (Punktlandung) | ✅ |
| F-015 | Sortiermodus (Drag & Drop, Touch) | `SortableList` (Pointer-Events = Maus + Touch) + Pfeil-Schaltflächen | E: „Sortiermodus: Tasten und Drag & Drop …“ | ✅ |
| F-020 | Schulden erfassen | `/schulden`, `DebtDialog` (Rate als Budgetposition, B-07) | D: „F-020: …“ · S: „Abhängige Änderungen …“ | ✅ |
| F-021 | Tilgung verbuchen (Annuität) | `DebtCalculator.Split`, `PaymentDialog`, automatische Buchung fälliger Raten | D: „F-021: …“ (3), „Automatische Buchung …“, „Leasing mit Restwert …“ | ✅ |
| F-022 | Fortschrittsanzeige Schulden | Fortschrittsbalken je Schuld + gesamt | D: „F-022: 50 % Tilgung = halbe Füllung …“ | ✅ |
| F-030 | Monatskalender Cashflow | `/cashflow` Kalender/Liste, Tagesdetails | D: „F-030: …“ (3) | ✅ |
| F-031 | Laufender Kontostand | `CashflowCalculator`, Kontostand-Abgleich (I-03) | D: „F-031: …“, „I-03: Rückwärtsrechnung …“ | ✅ |
| F-032 | Monats-/Jahresnavigation | `MonthSwitcher` (±36 Monate), Jahresnavigation in der Statistik | D: „F-032: …“ | ✅ |
| F-040 | Polster-Ziele | `/polster`, `BufferDialog` (Ziel, Zieldatum, Sparplan, Notgroschen-Vorschlag B-16) | D: „F-040: …“ (2) | ✅ |
| F-041 | Polster-Fortschritt | Fortschritt, Prognose, Verlauf | D: „F-041: 250/1000 € → 25 % …“ | ✅ |
| F-042 | Entnahme aus Polster | `BufferTxDialog` (Grund Pflicht, max. Stand) | D: „F-042 …“ (2) · S: „F-042: Entnahme max. …“ | ✅ |
| F-043 | Tilgungs-/Sparprognose | Durchschnitts-Extrapolation, aktualisiert sich automatisch | D: „F-043: …“ (4) | ✅ |
| F-050 | Dashboard / Statusleiste | `/uebersicht`, `KpiBar` (B-18) | D: „F-014 … F-050: Überschuss …“ · E: DoD | ✅ |
| F-051 | Ampelsystem | `AmpelEvaluator` (konfigurierbar, `/haushalt`), Farbe + Symbol + Text | D: „F-051: …“ (2), „Kategorie-Ampel …“ · E: axe (Kontrast) | ✅ |
| F-060 | Ausgaben nach Kategorie | Ringdiagramm mit Klick auf Einzelpositionen | D: „F-060: …“ · E: DoD (Summe 2.700 €) | ✅ |
| F-061 | Einnahmen-/Ausgaben-Verlauf | Liniendiagramm 3/6/12/24 Monate, Lücken statt 0 € | D: „F-061: …“ | ✅ |
| F-062 | 50/30/20-Analyse | Monat und Ø 6 Monate, Hinweis > 5 Punkte | D: „F-062: …“ · E: DoD (100,0 %) | ✅ |
| F-063 | Sparquote | Trend + Ø 3 Monate | D: „F-063: …“ | ✅ |
| F-064 | Top-Ausgaben | Absteigende Liste mit Anteil | D: „F-064: …“ | ✅ |
| F-065 | Jahresübersicht/-vergleich | Matrix, Vorjahr nur über gemeinsame Monate (I-19) | D: „F-065: …“ | ✅ |
| F-066 | Soll/Ist-Abweichung | Tabelle je Kategorie, Bewertung mit Symbol + Text | D: „F-066: …“ | ✅ |
| F-067 | Fixkostenquote | Kennzahl + Warnung > 60 % | D: „F-067: …“ | ✅ |
| F-068 | Vermögensübersicht | Polster − Restschulden, ≥ 12 Monate | D: „F-068: …“ | ✅ |
| F-070 | Kontostand-Prognose | `/prognosen`, Tagesverlauf gestrichelt in eigener Farbe | D: „F-070: …“ · E: DoD | ✅ |
| F-071 | Schuldenfreiheits-Prognose | Annuität/linear, Schneeball/Lawine | D: „F-071: …“ (4), „B-10: Schneeball …“ | ✅ |
| F-072 | Sparziel-Prognose | Ø 3 Monate inkl. geplanter Zuweisung | D: „F-072: …“, „F-043/F-072 …“ | ✅ |
| F-073 | Budget-Frühwarnung | Warnliste (Übersicht, Prognosen), Benachrichtigungen | D: „F-073: …“ (2) | ✅ |
| F-074 | Jahresend-Prognose | Ø × 12 + Einmalzahlungen exakt | D: „F-074: …“ | ✅ |
| F-075 | Szenario-Simulation | `ScenarioEditor` (9 Änderungsarten), Vergleich, Verwerfen/Übernehmen | D: „F-075: …“ (2) · E: DoD (Simulation + Verwerfen, echte Daten unverändert) | ✅ |
| F-080 | Monatsstart-Erinnerung | `NotificationEvaluator.MonthStartRemindersAsync` (In-App/Push/E-Mail) | S: „F-080: Monatsstart-Erinnerung genau einmal …“ | ✅ |
| F-081 | Warnbenachrichtigungen | 11 Arten × 3 Kanäle einzeln schaltbar (`/profil`, `/benachrichtigungen`) | S: „F-081: …“ (2) | ✅ |
| F-090 | Tab-Navigation mobil | Tab-Leiste < 768 px, Kopfzeile fix | E: „Mobile Tab-Leiste und Touch-Ziele“ | ✅ |
| F-091 | PWA-Installation | Manifest, Icons, iOS-Splash, Service Worker (Offline-Cache, Push, Update) | Skript `tests/e2e/scripts/release-offline-check.cjs` (Release-Build: Neustart offline mit Daten) · **Gerätetest Android/iOS: manuell** | 🟨 |
| F-092 | Touch-Optimierung | ≥ 44 px Ziele, Eingaben ≥ 16 px | E: „Mobile Tab-Leiste und Touch-Ziele“ | ✅ |
| F-100 | Profilverwaltung | `/profil` (Name, E-Mail mit Bestätigung, Sprache, Design, Zeitzone, Währung) | S: „F-100/F-101: …“ | ✅ |
| F-101 | Monatsstart-Tag | `PeriodCalendar` mit Regelhistorie | D: „F-101: …“ (4) · S: „F-100/F-101 …“ | ✅ |
| F-110 | Datenexport CSV/Excel | `/daten` (xlsx, CSV-ZIP, JSON) | S: „F-110: Export … < 10 Sekunden …“ | ✅ |
| F-111 | Konto-/Datenlöschung | `/profil` (Bestätigung durch Eingabe), Haushalt löschen | S: „F-111: Konto löschen …“ | ✅ |
| F-120 | Tastatur/Screenreader | Fokusfalle, Esc, ARIA, Fokus auf Überschrift nach Navigation | E: „Ausgabe nur mit der Tastatur anlegen“, axe | ✅ |
| F-121 | Kontrast/Skalierung | WCAG AA (Tokens geprüft), Ampel mit Symbol + Text | E: „Kein horizontales Scrollen bei 200 % Zoom und 320 px“, „Barrierefreiheit (axe, light/dark)“ | ✅ |
| F-130 | REST-API (JWT, versioniert) | `/api/v1/*`, OpenAPI `/openapi/v1.json`, Swagger `/api/docs`, API-Tokens | S: „F-130: …“ (2), „DoD: OpenAPI …“, „REST: ETag/If-Match …“ | ✅ |
| F-131 | Mehrgeräte-Sync | SignalR + Delta-Pull, feldweiser 3-Wege-Merge, geordnete Outbox | S: „F-131: …“ (3) · E: „Echtzeit-Synchronisation zwischen zwei Geräten“ (< 5 s) | ✅ |
| F-132 | Offline-Fähigkeit | IndexedDB + Outbox, Offline-Hinweis | E: „Offline anlegen, online synchronisieren“ (< 30 s) · Release-Skript (Neustart offline) | ✅ |
| F-133 | Daten-Import | `/daten` Assistent: Datei → Zuordnung → Prüfung (Zeilen überspringbar) → Import | S: „F-133: Import mit Spaltenzuordnung …“ | ✅ |
| F-140 | Geführte Ersteinrichtung | `OnboardingWizard` (Monatsstart, Einnahmen, Fixkosten, Notgroschen), überspringbar, im Profil/Hilfe erneut startbar | E: DoD (Assistent in < 1 Minute durchlaufen) | ✅ |
| F-141 | Kontext-Hilfetexte | `InfoTip` an Fachbegriffen, 46 Glossarbegriffe, schließt bei Klick außerhalb/Esc | E: axe; Glossar `/glossar`, `/hilfe` | ✅ |
| F-142 | Demodaten | Zweisprachige Beispieldaten in allen Modulen, mit einem Klick löschbar | D + S: „F-142: …“ · E: alle Responsive-/axe-Tests nutzen Demodaten | ✅ |

## BudgetHeld-Zusatzfunktionen (B-01 – B-22)

| ID | Umsetzung | Nachweis |
|----|-----------|----------|
| B-01–B-05 | Bereiche als Unterkategorien, Monatszentrale + Zielzone, Delta-Editing, Rückgängig 30 s, leere Kategorien ausblenden | D: „B-03: …“, E: DoD |
| B-06/B-07 | Kreditarten inkl. Zinsbindung/Restwert, Tilgungsplan, Zusatztilgung, Rate = Budgetposition | D: Schulden-Tests, S: „Abhängige Änderungen …“ |
| B-08 | Engpass-Vorhersage (Cashflow, Prognosen) | D: „F-031 …“ |
| B-09 | Kategorie-Ampeln, Spar-/Investquote | D: „Kategorie-Ampel …“ |
| B-10 | 6 öffentliche Rechner (`/rechner`) | D: Rechner-Tests, E: „Finanzrechner ohne Anmeldung“ |
| B-11/B-12/B-13 | Glossar (46 Begriffe), Deutsch/Englisch, 10 Währungen | `tools/check_i18n.py` (alle Schlüssel DE/EN) |
| B-14 | Teilen (Web Share API, WhatsApp, Facebook, X, LinkedIn) | Landing, Rechner, Einladungen |
| B-15 | Passkeys | `/profil` → Anmeldung & Sicherheit |
| B-16 | Notgroschen-Vorschlag (Faktor konfigurierbar) | Polster, Assistent |
| B-17 | Verschlüsselte Speicherung | AES-GCM-Spaltenverschlüsselung |
| B-18–B-22 | Statusleiste, Version im Footer + `/neuigkeiten`, mobile Einzeilen-Positionen, Sync-Fehlerhinweise, Feedback (`/hilfe`) | E: Mobile/axe |

## Erweiterungen über das Lastenheft hinaus (V-01 – V-05)

| ID | Umsetzung | Nachweis |
|----|-----------|----------|
| V-01–V-04 | Verwaltung `/verwaltung` (nur `App:AdminEmails`): Status, Registrierung offen/nur Freigegebene/geschlossen, Sperren/Entsperren/Löschen, Sicherung | S: „Verwaltung: nur für Administratoren …“, „… Registrierung offen / nur Freigegebene …“, „… Sperren beendet Sitzungen …“ · E: „Verwaltung: …“ (inkl. axe, 320 px) |
| V-05 | Betrieb auf dem Raspberry Pi hinter Tailscale Funnel (`deploy/pi`) | Cross-Build `linux-arm64` (ELF AArch64, SQLite nativ, `wwwroot`) · Anleitung `deploy/pi/README.md` |

## Nicht-funktionale Anforderungen

| Bereich | Anforderung | Nachweis | Status |
|---------|-------------|----------|--------|
| Performance | Laden < 3 s | Release-Build lokal: Kaltstart bis bedienbar 0,9 s, Warmstart 0,9 s, Offline-Neustart 2,4 s | ✅ |
| Performance | Berechnung < 300 ms bei 10.000 Positionen | D: „NFR Performance: 10.000 Positionen …“ (≈ 100 ms) | ✅ |
| Sicherheit | TLS, Verschlüsselung, Auth, Härtung | `docs/ABNAHME.md` → Sicherheitsreview; S: „Sicherheit: Push-Abos …“ | ✅ |
| Verfügbarkeit | Tägliche Sicherung, 14 Tage | `MaintenanceWorker` (`VACUUM INTO`, Aufbewahrung konfigurierbar) | ✅ |
| Kompatibilität | 320–2560 px, aktuelle Browser, PWA | E: 320/640/1440 px, Pixel 7; Chromium getestet; Safari/Firefox: manuell | 🟨 |
| Barrierefreiheit | WCAG 2.1 AA, 200 % Zoom | E: axe (hell/dunkel), Zoom, Tastatur | ✅ |
| Wartbarkeit | Regeln ohne Kernänderung konfigurierbar | `AmpelRules` je Haushalt (`/haushalt`) | ✅ |
| Datenschutz | DSGVO | `docs/ABNAHME.md` → DSGVO-Checkliste | ✅ |

## Definition of Done

| Kriterium | Nachweis | Status |
|-----------|----------|--------|
| Alle F-001–F-142 umgesetzt, AK nachweislich erfüllt | Tabelle oben | ✅ |
| PWA auf mind. einem Android- und iOS-Gerät getestet | Anleitung im README („Auf dem Smartphone testen“) – **manueller Schritt** | 🟨 |
| NFR per Lasttest/Sicherheitsreview/DSGVO-Checkliste abgenommen | `docs/ABNAHME.md` | ✅ |
| Durchlauf „Haushalt anlegen → Budget füllen → Unverplant zuweisen → Monat abschließen → Statistik/Prognose prüfen“ fehlerfrei | E: „DoD-Gesamtdurchlauf“ (prüft zusätzlich: keine Konsolenfehler) | ✅ |
| REST-API vollständig dokumentiert (OpenAPI/Swagger) und extern nutzbar | S: „DoD: OpenAPI …“, „F-130: Persönlicher API-Zugang …“; `/api/docs` | ✅ |
