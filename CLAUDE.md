# BudgetPlaner – Hinweise für Claude Code

Familienbudget-PWA nach Lastenheft (F-001–F-142), Vorbild BudgetHeld. Kommunikation mit dem Nutzer auf **Deutsch**.

- Stack: .NET 10, Blazor WebAssembly (PWA, offline-first mit IndexedDB + Outbox), ASP.NET Core Minimal APIs,
  EF Core + SQLite (Spalten AES-GCM-verschlüsselt), SignalR (`/hubs/sync`).
- Projekte: `src/BudgetPlaner.Domain` (Rechenkern) · `Shared` (Verträge, Übersetzungen `Localization/*.de|en.json`) ·
  `Application` (Anwendungsfälle) · `Infrastructure` (EF, Mail, Push, Passkeys) · `Server` (API, Hosting) · `Client` (PWA).
- Doku: `README.md`, `docs/PROJEKT_STATUS.md`, `docs/ENTSCHEIDUNGEN.md`, `docs/ANFORDERUNGEN.md`, `docs/ABNAHME.md`.

## Betrieb auf dem Raspberry Pi

Maßgeblich ist **`deploy/pi/README.md`** (Schritt-für-Schritt-Anleitung). Skripte in `deploy/pi/`:
`install.sh` (Erstinstallation/erneut ausführbar), `tailscale.sh` (Funnel + öffentliche Adresse), `update.sh`
(Update mit automatischem Rollback), `backup.sh` / `restore.sh` (Sicherung inkl. Schlüssel).

- Dienst `budgetplaner` (systemd, Nutzer `budgetplaner`), lauscht nur auf `127.0.0.1:5000`.
  Programm `/opt/budgetplaner/app`, Daten `/var/lib/budgetplaner`, Einstellungen `/etc/budgetplaner/budgetplaner.env`.
- Öffentlich nur über **Tailscale Funnel** (`https://<gerät>.<tailnet>.ts.net`). Der Nutzer kennt Tailscale noch nicht:
  Schritte in einfachen Worten erklären. Browser-Schritte (Tailscale-Login, Gerät umbenennen, „Disable key expiry“,
  Funnel freischalten) macht der Nutzer selbst – den Link aus der Terminalausgabe nennen und warten.
- `sudo tailscale up` und `tailscale funnel` warten auf eine Bestätigung im Browser → im Hintergrund starten und die
  Ausgabe (Link) lesen, nicht blockierend warten.
- **Geheimnisse:** Die `.env`-Datei nie vollständig ausgeben, nichts daraus committen. Zum Anzeigen:
  `sudo grep -vE 'Password|Key' /etc/budgetplaner/budgetplaner.env`. Das SMTP-Passwort trägt der Nutzer selbst mit
  `sudo nano` ein – nicht im Chat erfragen.
- Schlüssel (`Security__EncryptionKey`, `Security__JwtSigningKey`) nie ändern oder neu erzeugen – sonst sind Daten
  unlesbar. Vor riskanten Schritten `backup.sh` ausführen.
- Diagnose: `systemctl status budgetplaner`, `journalctl -u budgetplaner -n 100`, `curl http://127.0.0.1:5000/health`,
  `tailscale status`, `tailscale funnel status`, `sudo ss -tlnp`.
- Registrierung ist auf dem Pi „Auf Anfrage“ voreingestellt (`App__DefaultRegistrationMode=approval`): Neue stellen
  eine Anfrage, der Admin schaltet unter `/verwaltung` frei. Mailversand über ein beliebiges Postfach (Tabelle in der
  Anleitung, Schritt 3) – Outlook/Hotmail geht nicht.
- Auf dem Pi laufen außerdem Mycodo (nginx auf 80/443), ein Discord-Bot und iroh – diese nicht anfassen.

## Entwicklung

```bash
dotnet build BudgetPlaner.slnx
dotnet run --project src/BudgetPlaner.Server --launch-profile http     # http://localhost:5278, Postfach /dev/postfach
dotnet test tests/BudgetPlaner.Domain.Tests
dotnet test tests/BudgetPlaner.Server.Tests
cd tests/e2e && npx playwright test                                    # nach dotnet build; eigener Server auf :5299
python tools/check_i18n.py                                             # Übersetzungsschlüssel DE/EN
dotnet ef migrations add <Name> -p src/BudgetPlaner.Infrastructure -s src/BudgetPlaner.Server -o Persistence/Migrations
```

- Neue Texte immer in **beiden** Sprachdateien anlegen (`*.de.json` und `*.en.json`).
- Razor: kein `""` in Attribut-Lambdas (`string.Empty`), Komponentenparameter mit Anführungszeichen als `@(...)`.
- Dialogzustand in `OnInitialized` setzen, nicht in `OnParametersSet` (Sync-Rerender setzt Eingaben sonst zurück).
- Anonyme API-Aufrufe im Client: `Api.GetAsync<T>(url, auth: false)`.
- Admin-Funktionen: `App:AdminEmails`; Endpunkte unter `/api/v1/admin`, Seite `/verwaltung`.
