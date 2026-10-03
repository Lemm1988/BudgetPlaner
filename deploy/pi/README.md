# BudgetPlaner auf dem Raspberry Pi betreiben

Diese Anleitung bringt BudgetPlaner auf einen Raspberry Pi (getestet für Pi 4, 8 GB) – neben Mycodo, einem
Discord-Bot oder anderen Diensten. Danach können bis zu ~10 Personen die App über eine feste HTTPS-Adresse auf ihr
Handy installieren. Zeitbedarf: ca. 45 Minuten, davon 15–20 Minuten Warten auf den ersten Build.

```
Handy / PC (Browser, installierte App)
        │  https://budget.tailXXXX.ts.net   (gültiges Zertifikat, öffentlich erreichbar)
        ▼
Tailscale Funnel ── verschlüsselter Tunnel, KEINE Portfreigabe im Router nötig
        │
        ▼
Raspberry Pi:  tailscaled ──▶ BudgetPlaner (Dienst „budgetplaner“, nur 127.0.0.1:5000)
                                 └─ Daten: /var/lib/budgetplaner  (SQLite, Beträge verschlüsselt)
                                 └─ Einstellungen + Schlüssel: /etc/budgetplaner/budgetplaner.env
```

## 0. Was du brauchst

- Raspberry Pi 3/4/5 mit **Raspberry Pi OS 64 Bit** (Bookworm oder neuer). Prüfen: `uname -m` → `aarch64`.
  32 Bit (`armv7l`) funktioniert auch, ist aber langsamer.
- ~2 GB freier Speicher (`df -h /`), Internetzugang, Zugriff per Terminal/SSH mit `sudo`-Rechten.
- Ein **E-Mail-Postfach zum Versenden** der Anmeldecodes – eine ganz normale Adresse bei Gmail, GMX, WEB.DE, IONOS
  o. Ä. reicht. Ohne E-Mail kann sich niemand anmelden – es gibt keine Passwörter.
- Ein kostenloses **Tailscale-Konto** (Anmeldung mit Google, Microsoft, GitHub oder Apple) – wird in Schritt 4 erklärt.
- Zugriff auf das private GitHub-Repository `Lemm1988/BudgetPlaner`.

Speicherbedarf im Betrieb: ca. 150–250 MB RAM (der Dienst ist auf 700 MB begrenzt). Das verträgt sich gut mit
Mycodo und einem Discord-Bot.

## 1. Repository auf den Pi holen

Das Repository ist privat, darum braucht der Pi einmalig eine Anmeldung bei GitHub. Am einfachsten mit der GitHub-CLI:

```bash
sudo apt update && sudo apt install -y gh git
gh auth login            # GitHub.com → HTTPS → „Login with a web browser“
                         # → angezeigten Code auf github.com/login/device am PC eingeben
gh repo clone Lemm1988/BudgetPlaner ~/BudgetPlaner
cd ~/BudgetPlaner
```

> Alternative ohne gh: einen **Deploy Key** (nur Lesezugriff) anlegen – `ssh-keygen -t ed25519 -f ~/.ssh/budgetplaner`,
> den Inhalt von `~/.ssh/budgetplaner.pub` auf GitHub unter Repository → Settings → Deploy keys eintragen und mit
> `GIT_SSH_COMMAND="ssh -i ~/.ssh/budgetplaner" git clone git@github.com:Lemm1988/BudgetPlaner.git ~/BudgetPlaner` klonen.

## 2. Installieren

```bash
cd ~/BudgetPlaner
sudo bash deploy/pi/install.sh --admin deine@adresse.de
```

`--admin` ist die E-Mail-Adresse, mit der **du** dich später anmeldest – sie bekommt den Verwaltungsbereich.
Das Skript

1. installiert fehlende Pakete und das .NET-10-SDK (nur zum Bauen, nach `/opt/dotnet`),
2. legt den Systemnutzer `budgetplaner` und die Verzeichnisse an,
3. erzeugt `/etc/budgetplaner/budgetplaner.env` mit zufälligen Schlüsseln (nur für root und den Dienst lesbar),
4. baut die App (beim ersten Mal 5–15 Minuten) nach `/opt/budgetplaner/app`,
5. richtet den abgesicherten systemd-Dienst `budgetplaner` ein und startet ihn.

Prüfen:

```bash
systemctl status budgetplaner          # „active (running)“
curl http://127.0.0.1:5000/health      # {"status":"ok"}
```

Ist Port 5000 schon belegt (z. B. durch einen anderen Dienst), meldet das Skript es – dann mit `--port 5080`
installieren. Welche Ports belegt sind, zeigt `sudo ss -tlnp`.

## 3. E-Mail-Versand einrichten

Die App verschickt Anmeldecodes über ein **ganz normales E-Mail-Postfach** – Gmail, GMX, WEB.DE, IONOS usw.

> **Tipp:** Lege dafür eine **eigene, kostenlose Adresse** an (z. B. `budget.familie.muster@gmx.de`) statt deiner
> privaten Hauptadresse. Die Empfänger sehen diese Adresse als Absender, und falls doch einmal etwas schiefgeht
> (Versandlimit, Spam-Verdacht), ist dein eigenes Postfach nicht betroffen. Für 10 Personen reichen die Limits der
> Gratis-Anbieter (z. B. Gmail ~500 Mails/Tag) bei Weitem.

Die Datei mit den Einstellungen öffnest du **selbst** (das Passwort gehört nicht in Chats oder Notizen):

```bash
sudo nano /etc/budgetplaner/budgetplaner.env
```

Diese Zeilen ausfüllen (Beispiel GMX):

```ini
Smtp__Host=mail.gmx.net
Smtp__Port=587
Smtp__Security=StartTls
Smtp__User=budget.familie.muster@gmx.de
Smtp__Password="dein Passwort"
```

Speichern mit `Strg+O`, `Enter`, beenden mit `Strg+X`, dann `sudo systemctl restart budgetplaner`.

| Anbieter | `Smtp__Host` | Besonderheit |
|----------|--------------|--------------|
| Gmail | `smtp.gmail.com` | Passwort = **App-Passwort** (Google-Konto → Sicherheit → Bestätigung in zwei Schritten aktivieren → App-Passwörter) |
| GMX | `mail.gmx.net` | In GMX zuerst erlauben: Einstellungen → POP3/IMAP Abruf → „Senden und Empfangen von E-Mails über externe Programme“ |
| WEB.DE | `smtp.web.de` | wie GMX |
| IONOS | `smtp.ionos.de` | Passwort des Postfachs |
| T-Online | `securesmtp.t-online.de` | eigenes **E-Mail-Passwort** (im Kundencenter festlegen) |

- Bei allen: Port 587, `StartTls`, Benutzer = die vollständige Adresse.
- `Smtp__From` kannst du leer lassen – dann ist das Postfach selbst der Absender (fremde Absender lehnen die Anbieter ab).
- Outlook/Hotmail-Adressen funktionieren nicht: Microsoft erlaubt dort keine Anmeldung per Passwort aus Programmen mehr.
- Enthält das Passwort `"` oder `\`, diese als `\"` bzw. `\\` schreiben.

Ob der Versand klappt, siehst du in Schritt 5 (die Anmeldemail kommt an) und im Protokoll:
`journalctl -u budgetplaner -n 50`.

## 4. Tailscale einrichten (öffentliche HTTPS-Adresse)

### Was ist Tailscale, und warum?

Eine installierbare Web-App (PWA) braucht **HTTPS mit gültigem Zertifikat** – sonst lässt sie sich nicht
installieren und Passkeys/Push funktionieren nicht. Normalerweise bräuchte man dafür eine eigene Domain, eine
Portfreigabe im Router und ein Zertifikat. Tailscale nimmt dir das ab:

- **Tailscale** verbindet deine Geräte über ein privates, verschlüsseltes Netz („Tailnet“). Jedes Gerät bekommt
  einen Namen wie `budget.tailXXXX.ts.net`.
- **Funnel** ist eine Tailscale-Funktion, die *einen* Dienst des Pi unter dieser Adresse **öffentlich** ins Internet
  stellt – mit echtem Let's-Encrypt-Zertifikat. Die Verbindung baut der Pi selbst nach außen auf; im Router wird
  nichts freigegeben, deine Heim-IP bleibt verborgen.
- Deine 10 Nutzer brauchen **kein** Tailscale – für sie ist es eine ganz normale Webseite.
- Kostenlos im Personal-Plan. Mycodo, der Discord-Bot und iroh laufen unverändert weiter.

### 4.1 Konto anlegen

Am PC <https://login.tailscale.com/start> öffnen und mit Google, Microsoft, GitHub oder Apple anmelden.
Bei der Frage nach dem ersten Gerät kannst du „Linux“ wählen oder den Assistenten überspringen.

### 4.2 Tailscale auf dem Pi installieren und anmelden

```bash
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up
```

`tailscale up` zeigt einen Link (`https://login.tailscale.com/a/…`). Den am PC öffnen, mit demselben Konto anmelden
und das Gerät bestätigen. Danach meldet das Terminal „Success“. Prüfen: `tailscale status`.

### 4.3 Gerätenamen festlegen und Ablauf abschalten (wichtig)

In der Tailscale-Verwaltung <https://login.tailscale.com/admin/machines> beim Pi auf **„…“** klicken:

1. **„Edit machine name…“** → z. B. `budget`. Der Name bestimmt die Adresse (`https://budget.tailXXXX.ts.net`).
   Lege ihn **jetzt** fest: Passkeys der Nutzer sind an die Adresse gebunden, ein späterer Wechsel macht sie ungültig.
2. **„Disable key expiry“** → sonst muss sich der Pi nach 180 Tagen neu anmelden und die App ist so lange offline.

Unter **DNS** sollte „MagicDNS“ aktiv sein (bei neuen Konten Standard).

### 4.4 Funnel einschalten

```bash
cd ~/BudgetPlaner
sudo bash deploy/pi/tailscale.sh
```

Das Skript schaltet Funnel für den Dienst ein, trägt die Adresse in die Einstellungen ein, startet die App neu und
prüft die Verbindung. Beim ersten Mal erscheint evtl. ein Link „…to enable, visit: https://login.tailscale.com/f/…“ –
im Browser öffnen, HTTPS bzw. Funnel bestätigen, das Skript läuft danach von selbst weiter.

Test: auf dem Handy **WLAN aus**, mobile Daten an, Adresse öffnen → die Startseite von BudgetPlaner erscheint.

Hinweise:

- `tailscale funnel status` zeigt, was veröffentlicht ist. Ausschalten: `sudo bash deploy/pi/tailscale.sh --off`.
- Mycodo bleibt im Heimnetz unter seiner bisherigen Adresse erreichbar. Nur wer den Pi **über Tailscale** auf Port 443
  aufruft, landet jetzt bei BudgetPlaner.

## 5. Erste Anmeldung und Registrierung festlegen

1. Adresse aufrufen → **Anmelden** → deine Admin-Adresse eingeben → Code aus der E-Mail eingeben.
2. Im Menü erscheint **Verwaltung** (bzw. Avatar oben rechts → Verwaltung). Dort siehst du Serverstatus,
   E-Mail/Push-Status, Sicherungen und alle Konten.
3. **Registrierung** – auf dem Pi ist **„Auf Anfrage“** voreingestellt. In der Verwaltung jederzeit umschaltbar:
   - **Offen** – jeder, der die Adresse kennt, kann sofort ein Konto anlegen.
   - **Auf Anfrage** *(voreingestellt, empfohlen)* – wer neu ist, gibt einfach die eigene E-Mail-Adresse ein und stellt
     damit eine Anfrage. Dabei wird **keine** Mail verschickt. Du bekommst einen Hinweis per Mail, klickst in der
     Verwaltung auf **Freischalten**, und erst dann bekommt die Person eine Mail mit dem Anmeldelink.
   - **Nur Freigegebene** – nur Adressen/Domains, die du einträgst, und Eingeladene; alle anderen werden abgewiesen.
   - **Geschlossen** – keine neuen Konten (außer Admins). Bestehende Konten melden sich weiter an.

   Wer schon ein Konto hat, meldet sich immer ganz normal an. Wer zu einem Haushalt eingeladen wurde, kommt in allen
   Modi außer „Geschlossen“ direkt hinein.

> **Warum nicht „Offen“?** Funnel-Adressen sind öffentlich und tauchen in Zertifikatsverzeichnissen auf – Bots
> finden sie. Bei offener Registrierung könnte jeder Fremde deinen Server Mails an beliebige Adressen schicken lassen.
> Das schadet dem Ruf deines Postfachs, im schlimmsten Fall sperrt der Anbieter es. Eine klassische Registrierung mit
> Passwort hilft dagegen nicht: Auch sie muss die Adresse per Mail bestätigen. Darum entscheidet bei „Auf Anfrage“
> erst ein Mensch, bevor eine Mail an eine neue Adresse geht. Einzelne Konten kannst du jederzeit **sperren**
> (sofort auf allen Geräten abgemeldet) oder **löschen**.

## 6. Nutzer einladen und App aufs Handy

- Adresse weitergeben. Bei „Auf Anfrage“ geben die Leute dort ihre E-Mail-Adresse ein, du schaltest sie in der
  Verwaltung frei, und sie bekommen eine Mail mit dem Anmeldelink. (Bei „Nur Freigegebene“ trägst du die Adressen
  vorher selbst ein.)
- Wer **gemeinsam** einen Haushalt führt: Eine Person lädt die anderen unter **Haushalt → Mitglied einladen** ein
  (per E-Mail oder Link). Eingeladene brauchen keine Freischaltung (außer im Modus „Geschlossen“).
- **Android (Chrome):** Adresse öffnen → Menü ⋮ → **App installieren**.
- **iPhone (Safari):** Adresse öffnen → Teilen-Symbol → **Zum Home-Bildschirm**. Push-Mitteilungen gibt es auf dem
  iPhone nur in der installierten App (ab iOS 16.4).
- Unter **Profil → Passkeys** lässt sich Fingerabdruck/Face ID einrichten – dann geht die Anmeldung ohne Mail.

## 7. Aktualisieren

```bash
cd ~/BudgetPlaner
sudo bash deploy/pi/update.sh
```

Holt den neuesten Stand von GitHub, baut ihn, sichert die Datenbank, schaltet um und prüft den Start. Startet die neue
Version nicht, wird **automatisch** die vorherige Version samt Datenbank wiederhergestellt. Die installierten Apps
zeigen beim nächsten Öffnen „Eine neue Version ist verfügbar.“ mit einem Knopf zum Neuladen.

Das Betriebssystem regelmäßig aktualisieren: `sudo apt update && sudo apt full-upgrade`.

## 8. Sicherungen

- **Automatisch:** täglich eine Kopie der Datenbank in `/var/lib/budgetplaner/backups` (14 Tage). Zusätzlich vor
  jedem Update und auf Knopfdruck in der Verwaltung („Jetzt sichern“).
- **Außerhalb des Pi (wichtig – SD-Karten gehen kaputt):**

  ```bash
  sudo bash deploy/pi/backup.sh /media/usb/budget-sicherung     # Ziel: USB-Stick, NAS-Freigabe …
  ```

  Das Archiv enthält Datenbank, **Schlüssel** und Einstellungen. Ohne die Schlüssel sind die Daten nicht lesbar –
  darum immer beides zusammen sichern und das Archiv sicher aufbewahren (es enthält auch das SMTP-Passwort).
  Wöchentlich automatisch: `sudo crontab -e` und diese Zeile eintragen (Pfade anpassen):
  `0 3 * * 0 bash /home/pi/BudgetPlaner/deploy/pi/backup.sh /media/usb/budget-sicherung`
- **Wiederherstellen** (z. B. auf neuer SD-Karte): Schritte 1 und 2 ausführen, dann
  `sudo bash deploy/pi/restore.sh /media/usb/budget-sicherung/budgetplaner-….tar.gz` und Schritt 4.4.

## 9. Was ist abgesichert?

| Maßnahme | Wirkung |
|----------|---------|
| App lauscht nur auf `127.0.0.1` | Im Heimnetz nicht direkt erreichbar; von außen nur über Funnel |
| Keine Portfreigabe im Router | Heim-IP bleibt verborgen, der Router bleibt zu |
| HTTPS über Tailscale (Let's Encrypt) | Verschlüsselte Verbindung, HSTS |
| systemd-Härtung | Eigener Nutzer ohne Login, nur `/var/lib/budgetplaner` beschreibbar, kein Zugriff auf `/home`, keine Rechteausweitung, Speicherlimit |
| Verschlüsselung in der Datenbank | Beträge, Bezeichnungen und Notizen sind mit AES-GCM verschlüsselt |
| Einstellungen nur für root/Dienst lesbar | Schlüssel und SMTP-Passwort sind für andere Nutzer des Pi nicht lesbar |
| Passwortlose Anmeldung | Codes 15 Minuten gültig, einmalig, max. 5 Versuche; Rate-Limits je Adresse und IP |
| Verwaltung | Registrierung einschränken, Konten sperren (sofortige Abmeldung überall) oder löschen |
| Entwicklungs-Postfach | in Produktion zwangsweise aus |

Empfehlungen: SSH nur mit Schlüssel (`PasswordAuthentication no`), System aktuell halten, Sicherungen außerhalb des Pi,
in Tailscale „Disable key expiry“ nur für diesen Pi.

Vor einer Öffnung über den Familien- und Freundeskreis hinaus: Impressum und Datenschutzerklärung ausfüllen (Platzhalter in
`src/BudgetPlaner.Shared/Localization/pages.de.json` bzw. `pages.en.json`) – am besten am PC ändern, nach GitHub
pushen und auf dem Pi `update.sh` ausführen.

## 10. Nützliche Befehle und Fehlerbehebung

```bash
systemctl status budgetplaner                 # läuft der Dienst?
journalctl -u budgetplaner -f                 # Protokoll live (Strg+C beendet)
journalctl -u budgetplaner -p warning -n 50   # nur Warnungen/Fehler
sudo systemctl restart budgetplaner           # nach Änderungen an der .env-Datei
systemctl show budgetplaner -p MemoryCurrent  # aktueller Speicherverbrauch
tailscale funnel status                       # was ist öffentlich?
sudo ss -tlnp                                 # welche Ports sind belegt?
```

| Problem | Lösung |
|---------|--------|
| Keine Anmeldemail | Schritt 3 prüfen; `journalctl -u budgetplaner -n 100` zeigt SMTP-Fehler (z. B. „Authentication failed“ → Passwort). Spam-Ordner prüfen. |
| Link in der Mail zeigt auf eine falsche Adresse | `App__PublicBaseUrl` stimmt nicht → `sudo bash deploy/pi/tailscale.sh` erneut ausführen. |
| Passkey funktioniert nicht | Passkeys gelten nur für genau die Adresse in `App__PublicBaseUrl`. Nach einer Adressänderung neu einrichten. |
| Adresse nicht erreichbar | `tailscale status` (verbunden?), `tailscale funnel status`, in der Verwaltung HTTPS/Funnel aktiv? Key expiry abgelaufen → `sudo tailscale up`. |
| „Registrierung eingeschränkt“ | In der Verwaltung Adresse freigeben oder per Haushaltseinladung einladen. |
| „Anfrage gesendet“, aber keine Freischalt-Mail | In der Verwaltung unter „Registrierungsanfragen“ freischalten; SMTP prüfen (Zeile „E-Mail-Versand“ im Status). |
| Dienst startet nicht | `journalctl -u budgetplaner -n 100`. Sollte eine Härtungsoption in `/etc/systemd/system/budgetplaner.service` stören: Zeile mit `#` auskommentieren, `sudo systemctl daemon-reload && sudo systemctl restart budgetplaner`. |
| Port belegt | `sudo bash deploy/pi/install.sh --port 5080`, danach `sudo bash deploy/pi/tailscale.sh`. |
| Admin-Menü fehlt | `App__AdminEmails` in der .env-Datei prüfen (genau die Anmeldeadresse), Dienst neu starten, neu anmelden. |

## 11. Deinstallieren

```bash
sudo bash deploy/pi/tailscale.sh --off
sudo systemctl disable --now budgetplaner
sudo rm /etc/systemd/system/budgetplaner.service && sudo systemctl daemon-reload
sudo rm -rf /opt/budgetplaner            # Programm
# Daten und Schlüssel erst löschen, wenn eine Sicherung existiert:
# sudo rm -rf /var/lib/budgetplaner /etc/budgetplaner && sudo userdel budgetplaner
```
