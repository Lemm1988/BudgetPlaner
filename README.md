# BudgetPlaner

Familienbudget als installierbare Web-App (PWA) – nach dem Vorbild von BudgetHeld und dem Lastenheft (F-001–F-142):
Budget nach der 50/30/20-Regel, Schulden mit Tilgungsplan, Cashflow-Kalender, Polster, Übersicht mit Ampeln,
Statistik, Prognosen mit Was-wäre-wenn-Szenarien, gemeinsamer Haushalt mit Echtzeit-Synchronisation, Offline-Modus,
sechs öffentliche Finanzrechner, Deutsch/Englisch, REST-API mit OpenAPI-Dokumentation.

**Technik:** .NET 10, Blazor WebAssembly (PWA), ASP.NET Core, EF Core + SQLite, SignalR.

---

## Schnellstart (lokal)

Voraussetzung: [.NET SDK 10](https://dotnet.microsoft.com/download) (getestet mit 10.0.4xx).

```bash
dotnet run --project src/BudgetPlaner.Server --launch-profile http
```

Dann <http://localhost:5278> öffnen und mit einer beliebigen E-Mail-Adresse anmelden. Im Entwicklungsmodus werden
keine echten E-Mails verschickt – der Anmeldecode steht im **Entwicklungs-Postfach** unter
<http://localhost:5278/dev/postfach>. Für einen schnellen Überblick im Einrichtungsassistenten
„Lieber erst mit Beispieldaten ausprobieren“ wählen.

| Adresse | Inhalt |
|---------|--------|
| `/` | Startseite |
| `/uebersicht`, `/budget`, `/schulden`, `/cashflow`, `/polster` | Hauptbereiche (mobil als Tab-Leiste) |
| `/statistik`, `/prognosen` | Auswertungen und Szenarien |
| `/rechner`, `/glossar` | öffentlich, ohne Anmeldung |
| `/api/docs` | Swagger-UI der REST-API, OpenAPI unter `/openapi/v1.json` |
| `/dev/postfach` | nur Entwicklung: empfangene E-Mails |

## Projektstruktur

```
src/
  BudgetPlaner.Domain          Rechenkern (Budget, Cashflow, Schulden, Polster, Statistik, Prognosen, Ampel, Szenarien, Rechner)
  BudgetPlaner.Shared          API-Verträge, Übersetzungen DE/EN (Localization/*.json), Formatierung
  BudgetPlaner.Application     Anwendungsfälle: Anmeldung, Haushalt, Synchronisation, Benachrichtigungen, Export/Import
  BudgetPlaner.Infrastructure  EF Core/SQLite, Verschlüsselung, E-Mail, Web-Push, Passkeys, Excel/CSV
  BudgetPlaner.Server          REST-API /api/v1, SignalR, Hintergrunddienste, Auslieferung des Clients
  BudgetPlaner.Client          Blazor-WebAssembly-PWA (Offline-Speicher IndexedDB, Service Worker)
tests/                         Unit-, Integrations- und End-to-End-Tests
docs/                          Projektstatus, Entscheidungen, Anforderungs-Nachweis, Abnahme
tools/                         i18n-Prüfung, Icon-Generator
```

Fachliche Entscheidungen und geklärte Unklarheiten des Lastenhefts: [docs/ENTSCHEIDUNGEN.md](docs/ENTSCHEIDUNGEN.md) ·
Nachweis je Anforderung: [docs/ANFORDERUNGEN.md](docs/ANFORDERUNGEN.md) · Abnahme (Performance, Sicherheit, DSGVO):
[docs/ABNAHME.md](docs/ABNAHME.md).

## Konfiguration

Einstellungen stehen in `src/BudgetPlaner.Server/appsettings.json` oder als Umgebungsvariablen
(Doppelpunkt durch `__` ersetzen, z. B. `App__PublicBaseUrl`).

| Schlüssel | Bedeutung | Standard |
|-----------|-----------|----------|
| `App:PublicBaseUrl` | Öffentliche Adresse (Links in E-Mails, Passkeys) | aus der Anfrage |
| `App:RequireHttps` | HTTPS-Umleitung + HSTS | `true` (Entwicklung: `false`) |
| `App:DataDirectory` | Datenbank, Schlüssel, Sicherungen | `App_Data` |
| `App:DevMailbox` | E-Mails ins Entwicklungs-Postfach – **nur in Development/Testing wirksam** | `false` |
| `App:BackgroundJobs` | Automatische Buchungen, Erinnerungen, Sicherungen | `true` |
| `App:MonthStartReminderHour` | Uhrzeit der Monatsstart-Erinnerung (Zeitzone des Nutzers) | `8` |
| `App:BackupRetentionDays` | Aufbewahrung der täglichen Sicherungen | `14` |
| `App:PushEndpointHosts` | zusätzliche erlaubte Push-Dienste | FCM, Mozilla, Windows, Apple |
| `ConnectionStrings:Default` | SQLite-Verbindung | `App_Data/budgetplaner.db` |
| `Smtp:Host`, `Port`, `User`, `Password`, `From`, `FromName`, `Security` | E-Mail-Versand (`StartTls`, `SslOnConnect`, `None`) | – |
| `Security:EncryptionKey` | Base64, ≥ 32 Byte – verschlüsselt Beträge, Bezeichnungen, Notizen | wird erzeugt |
| `Security:JwtSigningKey` | Base64, ≥ 64 Byte | wird erzeugt |
| `Push:VapidPublicKey`, `Push:VapidPrivateKey`, `Push:Subject` | Web-Push-Schlüssel und Kontakt (`mailto:`) | wird erzeugt |
| `RateLimits:AuthPerMinute`, `RateLimits:ApiPerMinute` | Begrenzung je IP bzw. Nutzer | `20`, `1200` |

Fehlen die Schlüssel, erzeugt der Server sie beim ersten Start in `App_Data/keys` und schreibt eine Warnung ins Log.
**Diese Schlüssel unbedingt sichern** – ohne den Verschlüsselungsschlüssel sind Datenbank und Sicherungen nicht lesbar.
Kryptografisch sichere Schlüssel erzeugen, z. B. mit PowerShell 7:

```powershell
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(64))
```

oder `openssl rand -base64 64`.

## Betrieb (Produktion)

```bash
dotnet publish src/BudgetPlaner.Server -c Release -o publish
cd publish
ASPNETCORE_ENVIRONMENT=Production ./BudgetPlaner.Server --urls http://127.0.0.1:5000
```

- Davor einen Reverse-Proxy mit TLS setzen (z. B. Caddy: `caddy reverse-proxy --from budget.example.com --to 127.0.0.1:5000`).
  `X-Forwarded-*` wird von lokalen Proxys übernommen; WebSockets (SignalR, Pfad `/hubs/sync`) müssen durchgereicht werden.
- `App:PublicBaseUrl`, SMTP und die Schlüssel setzen (siehe oben).
- Die Datenbank wird beim Start automatisch migriert. Sicherungen entstehen täglich unter `App_Data/backups`
  (SQLite `VACUUM INTO`); Wiederherstellen = Server stoppen, `budgetplaner.db-wal`/`-shm` löschen, Sicherung als
  `budgetplaner.db` zurückkopieren (mit denselben Schlüsseln).
- Vor dem Livegang Impressum und Datenschutzerklärung ausfüllen (Platzhalter in
  `src/BudgetPlaner.Shared/Localization/pages.de.json` bzw. `pages.en.json`).

## Auf dem Smartphone testen (PWA)

Installation, Service Worker, Push und Passkeys funktionieren nur über **HTTPS** (Ausnahme: `localhost`).
Am einfachsten über einen Tunnel:

1. Server starten: `dotnet run --project src/BudgetPlaner.Server --launch-profile http`
2. Tunnel öffnen, z. B. `cloudflared tunnel --url http://localhost:5278` (oder `ngrok http 5278`).
   Die angezeigte `https://…`-Adresse auf dem Smartphone öffnen.
3. Für Links in E-Mails und Passkeys die Adresse eintragen: `App__PublicBaseUrl=https://…` und Server neu starten.
4. Anmeldecode im Entwicklungs-Postfach unter `https://…/dev/postfach` ablesen.
5. **Android (Chrome):** Menü ⋮ → „App installieren“. **iPhone (Safari):** Teilen → „Zum Home-Bildschirm“.

Prüfliste (DoD): App startet ohne Adressleiste mit Icon und Startbildschirm · nach einmaligem Laden im Flugmodus
starten → Daten sichtbar, Offline-Hinweis · Änderung offline anlegen, wieder online → wird übertragen ·
Push unter Profil → Benachrichtigungen aktivieren (iPhone: nur in der installierten App, ab iOS 16.4) ·
Tab-Leiste unten, Eingabefelder zoomen nicht.

## Tests

```bash
dotnet test tests/BudgetPlaner.Domain.Tests      # Rechenkern, Akzeptanzkriterien
dotnet test tests/BudgetPlaner.Server.Tests      # API, Sync, Sicherheit

# End-to-End (Playwright, startet einen eigenen Server mit leerer Datenbank auf Port 5299)
dotnet build BudgetPlaner.slnx
cd tests/e2e
npm ci
npx playwright install chromium
npx playwright test                              # Bericht: npx playwright show-report report

# Release-Build offline prüfen (Server zuvor veröffentlicht und mit ASPNETCORE_ENVIRONMENT=Development
# sowie --urls http://localhost:5300 gestartet)
npm run release-check

python tools/check_i18n.py                       # alle Übersetzungsschlüssel in DE und EN vorhanden?
```

## REST-API nutzen

1. Im Profil unter „API-Zugänge“ ein Token erstellen (wird nur einmal angezeigt).
2. Aufrufe mit `Authorization: Bearer <token>`, z. B.:

```bash
curl -H "Authorization: Bearer $TOKEN" https://budget.example.com/api/v1/months/2026-10
curl -H "Authorization: Bearer $TOKEN" "https://budget.example.com/api/v1/export?from=2026-01&to=2026-12&format=xlsx" -o export.xlsx
```

Alle Endpunkte mit Beschreibungen: `/api/docs`. Ohne Token antwortet die API mit `401`.
