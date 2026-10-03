#!/usr/bin/env bash
# BudgetPlaner auf dem Raspberry Pi installieren (Raspberry Pi OS / Debian, 64 Bit empfohlen).
#
# Aufruf aus dem geklonten Repository:
#   sudo bash deploy/pi/install.sh --admin du@example.com [--url https://budget.tailXXXX.ts.net] [--port 5000]
#
#   --admin  E-Mail-Adresse des Administrators (Pflicht bei der ersten Installation)
#   --url    öffentliche Adresse (setzt später auch deploy/pi/tailscale.sh automatisch)
#   --port   lokaler Port des Dienstes (Standard 5000, nur 127.0.0.1)
#
# Erneut ausführbar: Einstellungen in /etc/budgetplaner/budgetplaner.env und alle Daten bleiben erhalten.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

ADMIN=""
URL=""
PORT=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --admin) ADMIN="${2:-}"; shift 2 ;;
    --url) URL="${2:-}"; URL="${URL%/}"; shift 2 ;;
    --port) PORT="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "Unbekannte Option: $1 (Hilfe: --help)" ;;
  esac
done

require_root
[[ -f $REPO_DIR/BudgetPlaner.slnx ]] || die "BudgetPlaner.slnx nicht gefunden – bitte aus dem geklonten Repository starten."
PORT=${PORT:-$(installed_port)}
[[ $PORT =~ ^[0-9]+$ ]] || die "--port muss eine Zahl sein."
[[ -z $ADMIN || $ADMIN == *@*.* ]] || die "--admin erwartet eine E-Mail-Adresse."
[[ -z $URL || $URL == https://* ]] || die "--url muss mit https:// beginnen."
if [[ ! -f $ENV_FILE && -z $ADMIN ]]; then
  die "Bei der ersten Installation bitte den Administrator angeben: sudo bash $0 --admin du@example.com"
fi
RID=$(detect_rid)

say "Prüfungen"
info "System: $(. /etc/os-release && echo "$PRETTY_NAME"), Architektur $(uname -m) → $RID"
info "Arbeitsspeicher: $(free -m | awk '/^Mem:/ { print $2 " MB gesamt, " $7 " MB verfügbar" }')"
info "Freier Speicher in /opt: $(df -h /opt | awk 'NR == 2 { print $4 }')"
[[ $RID == linux-arm ]] && warn "32-Bit-System erkannt. Läuft, aber ein 64-Bit-Raspberry-Pi-OS ist schneller und besser unterstützt."
if ss -ltnH "sport = :$PORT" | grep -q . && ! systemctl is-active --quiet "$SERVICE"; then
  ss -ltnp "sport = :$PORT" || true
  die "Port $PORT ist bereits belegt. Anderen Port wählen, z. B.: sudo bash $0 --port 5080 …"
fi

say "Pakete installieren"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
ICU_PKG=$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -1 || true)
apt-get install -y -qq --no-install-recommends ca-certificates curl git openssl sqlite3 "${ICU_PKG:-libicu-dev}" >/dev/null
info "ca-certificates, curl, git, openssl, sqlite3, ${ICU_PKG:-libicu-dev}"

ensure_dotnet_sdk

say "Systemnutzer und Verzeichnisse"
if ! id -u "$APP_USER" >/dev/null 2>&1; then
  useradd --system --home-dir "$DATA_DIR" --no-create-home --shell /usr/sbin/nologin "$APP_USER"
  info "Nutzer $APP_USER angelegt."
fi
install -d -m 750 -o "$APP_USER" -g "$APP_USER" "$DATA_DIR"
install -d -m 755 -o root -g root "$APP_DIR"
install -d -m 750 -o root -g "$APP_USER" "$CONF_DIR"
info "$APP_DIR (Programm), $DATA_DIR (Daten), $CONF_DIR (Einstellungen)"

say "Einstellungen"
if [[ ! -f $ENV_FILE ]]; then
  install -m 640 -o root -g "$APP_USER" "$REPO_DIR/deploy/pi/budgetplaner.env.example" "$ENV_FILE"
  set_env App__PublicBaseUrl "$URL"
  set_env App__AdminEmails "$ADMIN"
  set_env App__DataDirectory "$DATA_DIR"
  set_env Push__Subject "mailto:${ADMIN%%,*}"
  set_env Security__EncryptionKey "$(openssl rand -base64 32 | tr -d '\n')"
  set_env Security__JwtSigningKey "$(openssl rand -base64 64 | tr -d '\n')"
  info "$ENV_FILE angelegt, Schlüssel erzeugt."
else
  [[ -n $ADMIN ]] && set_env App__AdminEmails "$ADMIN" && info "Administrator: $ADMIN"
  [[ -n $URL ]] && set_env App__PublicBaseUrl "$URL" && info "Adresse: $URL"
  info "$ENV_FILE bleibt erhalten."
fi
chown root:"$APP_USER" "$ENV_FILE"
chmod 640 "$ENV_FILE"

publish_app "$APP_DIR/app.new"
if [[ -d $APP_DIR/app ]]; then
  systemctl stop "$SERVICE" 2>/dev/null || true
  rm -rf "$APP_DIR/app.prev"
  mv "$APP_DIR/app" "$APP_DIR/app.prev"
fi
mv "$APP_DIR/app.new" "$APP_DIR/app"
install -m 644 "$REPO_DIR/deploy/pi/README.md" "$APP_DIR/README-Pi.md"

say "Dienst einrichten (systemd)"
sed "s/@PORT@/$PORT/g" "$REPO_DIR/deploy/pi/budgetplaner.service" > "$UNIT_FILE"
chmod 644 "$UNIT_FILE"
systemctl daemon-reload
systemctl enable --quiet "$SERVICE"
systemctl restart "$SERVICE"
if wait_healthy "$PORT"; then
  info "Dienst läuft: http://127.0.0.1:$PORT (nur lokal erreichbar)"
else
  journalctl -u "$SERVICE" -n 40 --no-pager || true
  die "Dienst startet nicht – Ausgabe oben prüfen (journalctl -u $SERVICE -n 100)."
fi

say "Fertig"
[[ -z $(get_env Smtp__Host) ]] && warn "E-Mail-Versand fehlt noch: sudo nano $ENV_FILE (Abschnitt Smtp__…), danach sudo systemctl restart $SERVICE"
if [[ -z $(get_env App__PublicBaseUrl) ]]; then
  warn "Öffentliche Adresse fehlt noch: Tailscale einrichten und dann sudo bash deploy/pi/tailscale.sh ausführen."
else
  info "Adresse: $(get_env App__PublicBaseUrl)"
fi
info "Administrator: $(get_env App__AdminEmails)"
info "Status: systemctl status $SERVICE · Protokoll: journalctl -u $SERVICE -f"
info "WICHTIG: $ENV_FILE enthält die Schlüssel – zusammen mit den Daten sichern (sudo bash deploy/pi/backup.sh <Ziel>)."
