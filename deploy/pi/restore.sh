#!/usr/bin/env bash
# Sicherung aus backup.sh wiederherstellen (z. B. nach SD-Karten-Defekt auf einem neu eingerichteten Pi).
# Vorher einmal install.sh ausführen, damit Dienst, Nutzer und Programm vorhanden sind.
#
# Aufruf:  sudo bash deploy/pi/restore.sh /media/usb/budget-sicherung/budgetplaner-JJJJMMTT-HHMMSS.tar.gz
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

ARCHIVE=${1:-}
require_root
[[ -f $ARCHIVE ]] || die "Archiv angeben, z. B.: sudo bash $0 /media/usb/budget/budgetplaner-20261003-030000.tar.gz"
id -u "$APP_USER" >/dev/null 2>&1 || die "Erst installieren: sudo bash deploy/pi/install.sh --admin du@example.com"

WORK=$(mktemp -d /tmp/budgetplaner-restore.XXXXXX)
trap 'rm -rf "$WORK"' EXIT
tar -xzf "$ARCHIVE" -C "$WORK"
[[ -f $WORK/data/budgetplaner.db && -f $WORK/config/budgetplaner.env ]] || die "Archiv unvollständig (Datenbank oder Einstellungen fehlen)."

say "Aktuellen Stand vorsorglich aufheben"
STAMP=$(date +%Y%m%d-%H%M%S)
systemctl stop "$SERVICE" 2>/dev/null || true
if [[ -f $DATA_DIR/budgetplaner.db ]]; then
  copy_database "$DATA_DIR/backups/vor-wiederherstellung-$STAMP"
  [[ -d $DATA_DIR/keys ]] && cp -a "$DATA_DIR/keys" "$DATA_DIR/backups/vor-wiederherstellung-$STAMP/"
fi
[[ -f $ENV_FILE ]] && cp -a "$ENV_FILE" "$CONF_DIR/budgetplaner.env.vor-wiederherstellung-$STAMP"

say "Wiederherstellen"
rm -f "$DATA_DIR"/budgetplaner.db "$DATA_DIR"/budgetplaner.db-wal "$DATA_DIR"/budgetplaner.db-shm
cp "$WORK/data/budgetplaner.db" "$DATA_DIR/"
if [[ -d $WORK/data/keys ]]; then
  rm -rf "$DATA_DIR/keys"
  cp -a "$WORK/data/keys" "$DATA_DIR/"
fi
chown -R "$APP_USER:$APP_USER" "$DATA_DIR"
install -m 640 -o root -g "$APP_USER" "$WORK/config/budgetplaner.env" "$ENV_FILE"
systemctl start "$SERVICE"
if wait_healthy; then
  info "Wiederhergestellt. Adresse: $(get_env App__PublicBaseUrl)"
  info "Hat sich die Adresse geändert (neuer Tailscale-Name)? Dann: sudo bash deploy/pi/tailscale.sh"
else
  journalctl -u "$SERVICE" -n 40 --no-pager || true
  die "Dienst startet nicht. Der vorherige Stand liegt in $DATA_DIR/backups/vor-wiederherstellung-$STAMP."
fi
