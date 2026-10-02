# Abnahme: Tests, Performance, Sicherheit, Datenschutz

Stand: 02.10.2026 · Version 1.0.0

## 1. Automatisierte Tests

| Suite | Umfang | Ergebnis |
|-------|--------|----------|
| `tests/BudgetPlaner.Domain.Tests` | Rechenkern: alle Akzeptanzkriterien mit Zahlenbeispielen, Rechner, Performance | 82/82 ✅ |
| `tests/BudgetPlaner.Server.Tests` | API, Anmeldung, Rollen, Sync/Merge, Export/Import, Benachrichtigungen, Löschung, Sicherheit | 28/28 ✅ |
| `tests/e2e` (Playwright, Chromium) | DoD-Gesamtdurchlauf, Tastatur, Offline, Echtzeit (2 Geräte), Mobil, Sortiermodus, 200 %-Zoom/320 px, axe (hell + dunkel), Rechner | 10/10 ✅ |
| `tools/check_i18n.py` | alle im Code verwendeten Übersetzungsschlüssel in DE und EN, keine Doppelungen | ✅ |
| `tests/e2e/scripts/release-offline-check.cjs` | Release-Build: Service Worker, Cache, Neustart ohne Netz | ✅ |

Ausführen siehe README, Abschnitt „Tests“.

## 2. Performance

| Messung | Ergebnis | Anforderung |
|---------|----------|-------------|
| 10.000 Positionen: Monat + 24-Monats-Statistik + Cashflow | ≈ 100 ms (Unit-Test, JIT) | < 300 ms ✅ |
| Release-Build, Kaltstart bis bedienbar (lokal) | 0,9 s | < 3 s ✅ |
| Warmstart (Service-Worker-Cache) bis Budget sichtbar | 0,9 s | < 3 s ✅ |
| Neustart ohne Netz mit Daten aus IndexedDB | 2,4 s | – |
| Export Excel/CSV, 12 Monate | innerhalb der Testgrenze von 10 s (Server-Test) | < 10 s ✅ |
| Echtzeit-Abgleich Gerät A → B | < 5 s (E2E-Test) | < 5 s ✅ |
| Offline-Änderung nach Wiederverbindung beim Server | < 30 s (E2E-Test) | < 30 s ✅ |

Ladezeiten sind lokal gemessen; über das Internet kommen Netzwerklatenz und die einmalige Übertragung von
≈ 12 MB (unkomprimiert; per Brotli ≈ 3,6 MB) beim allerersten Besuch hinzu. Folgestarts kommen aus dem Cache.

## 3. Sicherheitsreview

| Bereich | Umsetzung | Status |
|---------|-----------|--------|
| Transport | HTTPS-Pflicht + HSTS in Produktion (`App:RequireHttps`), Push/WebAuthn nur über HTTPS | ✅ |
| Anmeldung | Ohne Passwort: Magic Link / 6-stelliger Code (15 min, einmalig, 5 Versuche, 60 s Sperre je Adresse), Passkeys (FIDO2) | ✅ |
| Tokens | JWT HS256, 30 min; Refresh-Token rotierend (90 Tage); Sitzungen und API-Tokens bei jeder Anfrage gegen die DB geprüft → Abmelden/Widerruf wirkt sofort | ✅ |
| Autorisierung | Jeder Datensatz wird serverseitig dem Haushalt des Nutzers zugeordnet (Client-Angaben werden überschrieben); Owner-Funktionen geprüft | ✅ |
| Eingaben | Serverseitige Validierung aller Sync-Operationen, ProblemDetails ohne Interna, EF Core (parametrisiert), Import max. 10 MB | ✅ |
| Verschlüsselung ruhender Daten | AES-GCM je Spalte: Beträge, Bezeichnungen, Notizen, Gläubiger, Benachrichtigungstexte, Änderungsverlauf (E-Mail/Name für den Login im Klartext) | ✅ |
| Browser-Härtung | CSP ohne Inline-Skripte (`script-src 'self' 'wasm-unsafe-eval'`), WebSocket nur zum eigenen Host, `frame-ancestors 'none'`, `nosniff`, Referrer-/Permissions-Policy | ✅ |
| Missbrauch | Rate-Limits (Anmeldung je IP, API je Nutzer), Push-Abos nur an bekannte Push-Dienste (SSRF-Schutz) | ✅ |
| Geheimnisse | Schlüssel aus Konfiguration/Umgebung oder beim ersten Start in `App_Data/keys`; `App_Data` ist von Build, Veröffentlichung und Git ausgeschlossen | ✅ |
| Abhängigkeiten | `dotnet list package --vulnerable --include-transitive`: keine Funde; `npm audit`: 0 | ✅ |

**Im Review gefundene und behobene Punkte**

1. Das Entwicklungs-Postfach (`/dev/postfach`, zeigt Anmeldecodes) war nur per Konfiguration abgeschaltet → jetzt außerhalb
   von *Development*/*Testing* zwangsweise aus (Warnung im Log), Standardwert `false`.
2. Push-Abos akzeptierten jede `https://`-URL → der Server hätte Anfragen an interne Adressen gesendet (SSRF). Jetzt nur
   bekannte Push-Dienste (FCM, Mozilla, Windows, Apple), erweiterbar über `App:PushEndpointHosts`.
3. CSP `connect-src ws: wss:` erlaubte WebSockets zu beliebigen Hosts → auf den eigenen Host beschränkt.
4. `dotnet publish` hätte `App_Data/keys/vapid.json` (privater Push-Schlüssel) mit ausgeliefert → ausgeschlossen.

Restrisiken (bewusst akzeptiert, Standard bei SPAs): Tokens liegen im `localStorage` (Schutz: strikte CSP ohne
Fremd-Skripte); Swagger-UI ist öffentlich (enthält keine Daten).

## 4. DSGVO-Checkliste

| Punkt | Umsetzung | Status |
|-------|-----------|--------|
| Datenminimierung | Kein Bankzugang, keine Tracker/Analytics, keine Werbe-Cookies; nur E-Mail, Name und selbst eingetragene Werte | ✅ |
| Zweckbindung / Rechtsgrundlage | Vertragserfüllung (Art. 6 Abs. 1 lit. b) – siehe `/datenschutz` | ✅ |
| Auskunft & Übertragbarkeit (Art. 15/20) | Profil → „Meine Daten herunterladen (JSON)“; Export Excel/CSV/JSON unter `/daten` | ✅ |
| Löschung (Art. 17) | Profil → Konto löschen (sofort); Daten anderer Haushaltsmitglieder bleiben; Sicherungen nach ≤ 14 Tagen überschrieben | ✅ |
| Berichtigung (Art. 16) | Alle Daten in der App änderbar, Änderungsverlauf mit Wiederherstellung | ✅ |
| Sicherheit (Art. 32) | Siehe Sicherheitsreview; tägliche Sicherung | ✅ |
| Speicherung im Endgerät | Nur technisch notwendig (Offline-Modus); beim Abmelden gelöscht – in der Datenschutzerklärung beschrieben | ✅ |
| Informationspflichten | Datenschutzerklärung `/datenschutz`, Impressum `/impressum` – **Platzhalter vom Betreiber ausfüllen** | 🟨 |
| Auftragsverarbeitung | Hosting, SMTP- und Push-Dienst: AV-Verträge sind Sache des Betreibers | 🟨 |

## 5. Barrierefreiheit (WCAG 2.1 AA)

Automatisch geprüft (axe-core, alle Seiten, hell und dunkel): keine Verstöße der Stufen *serious*/*critical*.
Zusätzlich: vollständige Tastaturbedienung (Fokusfalle in Dialogen, Esc, sichtbarer Fokus), Fokus auf die Überschrift
nach Seitenwechsel, Ampeln mit Farbe + Symbol + Text, Diagramme mit Tabellenansicht und Tastatursteuerung,
Touch-Ziele ≥ 44 px, kein horizontales Scrollen bei 200 % Zoom und 320 px Breite.

## 6. Manuelle Schritte vor dem Livegang

1. **PWA auf echten Geräten testen** (Android + iPhone) – Anleitung im README.
2. **Impressum und Datenschutzerklärung** ausfüllen (Platzhalter in `src/BudgetPlaner.Shared/Localization/pages.*.json`).
3. **SMTP** konfigurieren (sonst keine echten E-Mails).
4. **Schlüssel** (`Security:EncryptionKey`, `Security:JwtSigningKey`, VAPID) sicher verwahren und mitsichern –
   ohne den Verschlüsselungsschlüssel sind Datenbank und Sicherungen nicht lesbar.
5. `App:PublicBaseUrl` auf die echte Adresse setzen (Links in E-Mails, Passkeys).
