#!/usr/bin/env bash
# Tailscale Funnel für BudgetPlaner einrichten: macht die App unter https://<gerät>.<tailnet>.ts.net öffentlich
# erreichbar (gültiges Zertifikat, keine Portfreigabe im Router, keine eigene Domain) und trägt die Adresse in die
# Einstellungen ein (Links in E-Mails, Passkeys).
#
# Voraussetzung (einmalig, siehe deploy/pi/README.md Schritt 4):
#   curl -fsSL https://tailscale.com/install.sh | sh
#   sudo tailscale up            → angezeigten Link im Browser öffnen und anmelden
#
# Aufruf:  sudo bash deploy/pi/tailscale.sh          Funnel einschalten / Adresse übernehmen
#          sudo bash deploy/pi/tailscale.sh --off    Funnel ausschalten (App nicht mehr öffentlich)
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

require_root
command -v tailscale >/dev/null || die "Tailscale fehlt. Installieren mit: curl -fsSL https://tailscale.com/install.sh | sh"
[[ -f $ENV_FILE ]] || die "BudgetPlaner ist noch nicht installiert – zuerst deploy/pi/install.sh ausführen."
PORT=$(installed_port)

if [[ ${1:-} == --off ]]; then
  tailscale funnel --https=443 off 2>/dev/null || tailscale funnel reset
  info "Funnel ausgeschaltet – die App ist nicht mehr aus dem Internet erreichbar."
  exit 0
fi

ts_json() { tailscale status --json | python3 -c "import json, sys; d = json.load(sys.stdin); print($1)"; }

STATE=$(ts_json 'd.get("BackendState", "")')
[[ $STATE == Running ]] || die "Tailscale ist nicht verbunden (Status: ${STATE:-unbekannt}). Erst ausführen: sudo tailscale up"
DNS=$(ts_json '(d.get("Self") or {}).get("DNSName", "").rstrip(".")')
[[ $DNS == *.ts.net ]] || die "Kein ts.net-Name gefunden – in der Tailscale-Verwaltung (DNS) „MagicDNS“ aktivieren."
URL="https://$DNS"

say "Funnel einschalten: $URL → http://127.0.0.1:$PORT"
info "Erscheint ein Link zum Freischalten von HTTPS bzw. Funnel: im Browser öffnen und bestätigen."
info "Das Skript wartet so lange und läuft danach selbst weiter."
tailscale funnel --bg "$PORT"
tailscale funnel status || true

if [[ $(get_env App__PublicBaseUrl) != "$URL" ]]; then
  say "Adresse in die Einstellungen übernehmen"
  set_env App__PublicBaseUrl "$URL"
  systemctl restart "$SERVICE"
  wait_healthy "$PORT" || die "Dienst startet nicht neu – journalctl -u $SERVICE -n 100"
  info "App__PublicBaseUrl=$URL"
fi

say "Verbindung prüfen (das erste Zertifikat kann bis zu einer Minute dauern)"
ok=0
for _ in $(seq 1 12); do
  if curl -fsS --max-time 20 "$URL/health" >/dev/null 2>&1; then ok=1; break; fi
  sleep 5
done
if [[ $ok == 1 ]]; then
  info "Erreichbar: $URL"
else
  warn "$URL antwortet noch nicht. Später erneut prüfen; Status: tailscale funnel status"
fi

say "Fertig"
info "Auf dem Handy (WLAN aus, mobile Daten) öffnen: $URL"
warn "Wichtig: In der Tailscale-Verwaltung (Machines → dieses Gerät → …) „Disable key expiry“ wählen,"
warn "sonst ist die App nach 180 Tagen nicht mehr erreichbar, bis du den Pi neu anmeldest."
