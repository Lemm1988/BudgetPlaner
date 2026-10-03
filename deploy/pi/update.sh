#!/usr/bin/env bash
# BudgetPlaner aktualisieren: neuesten Stand von GitHub holen, bauen, Datenbank sichern, umschalten, prüfen.
# Startet die neue Version nicht sauber, wird automatisch die vorherige Version samt Datenbank wiederhergestellt.
#
# Aufruf im Repository:  sudo bash deploy/pi/update.sh [--no-pull]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

PULL=1
[[ ${1:-} == --no-pull ]] && PULL=0
require_root
[[ -f $ENV_FILE && -d $APP_DIR/app ]] || die "BudgetPlaner ist noch nicht installiert – zuerst deploy/pi/install.sh ausführen."
PORT=$(installed_port)

if [[ $PULL == 1 ]]; then
  say "Neuesten Stand holen"
  git_repo pull --ff-only
fi
info "Version: $(git_repo log -1 --format='%h %s (%cd)' --date=short)"
ensure_dotnet_sdk

# Bauen, solange die alte Version noch läuft (kurze Ausfallzeit).
publish_app "$APP_DIR/app.new"

say "Umschalten"
STAMP=$(date +%Y%m%d-%H%M%S)
SNAPSHOT=$DATA_DIR/backups/vor-update-$STAMP
systemctl stop "$SERVICE"
copy_database "$SNAPSHOT"
chown -R "$APP_USER:$APP_USER" "$DATA_DIR/backups"
info "Datenbank gesichert: $SNAPSHOT"
rm -rf "$APP_DIR/app.prev"
mv "$APP_DIR/app" "$APP_DIR/app.prev"
mv "$APP_DIR/app.new" "$APP_DIR/app"
sed "s/@PORT@/$PORT/g" "$REPO_DIR/deploy/pi/budgetplaner.service" > "$UNIT_FILE"
install -m 644 "$REPO_DIR/deploy/pi/README.md" "$APP_DIR/README-Pi.md"
systemctl daemon-reload
systemctl start "$SERVICE"

if wait_healthy "$PORT"; then
  say "Update erfolgreich"
  info "Läuft auf http://127.0.0.1:$PORT – $(get_env App__PublicBaseUrl)"
  info "Die Apps auf den Handys laden die neue Version beim nächsten Öffnen (Hinweis „Neue Version verfügbar“)."
  # Alte Update-Sicherungen nach 30 Tagen entfernen.
  find "$DATA_DIR/backups" -maxdepth 1 -type d -name 'vor-update-*' -mtime +30 -exec rm -rf {} + 2>/dev/null || true
  exit 0
fi

warn "Neue Version startet nicht – stelle die vorherige Version wieder her."
journalctl -u "$SERVICE" -n 40 --no-pager || true
systemctl stop "$SERVICE" || true
rm -rf "$APP_DIR/app.failed"
mv "$APP_DIR/app" "$APP_DIR/app.failed"
mv "$APP_DIR/app.prev" "$APP_DIR/app"
rm -f "$DATA_DIR"/budgetplaner.db-wal "$DATA_DIR"/budgetplaner.db-shm
cp -a "$SNAPSHOT"/. "$DATA_DIR"/
chown "$APP_USER:$APP_USER" "$DATA_DIR"/budgetplaner.db*
systemctl start "$SERVICE"
if wait_healthy "$PORT"; then
  die "Update zurückgerollt – die vorherige Version läuft wieder. Fehlerausgabe oben; fehlerhafte Version liegt in $APP_DIR/app.failed."
fi
die "Auch die vorherige Version startet nicht. Bitte journalctl -u $SERVICE -n 200 prüfen."
