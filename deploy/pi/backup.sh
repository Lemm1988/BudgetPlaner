#!/usr/bin/env bash
# Vollständige Sicherung außerhalb des Pi: Datenbank (konsistente Kopie), Schlüssel und Einstellungen in ein Archiv.
# Ohne die Schlüssel ist die Datenbank nicht lesbar – darum gehört beides zusammen und das Archiv an einen sicheren Ort.
#
# Aufruf:  sudo bash deploy/pi/backup.sh /media/usb/budget-sicherung   (Zielordner, z. B. USB-Stick oder NAS)
# Regelmäßig (sonntags 3 Uhr) per root-Crontab:  0 3 * * 0 bash /pfad/zum/repo/deploy/pi/backup.sh /media/usb/budget
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

TARGET=${1:-}
KEEP=${KEEP:-8}
require_root
[[ -n $TARGET ]] || die "Zielordner angeben, z. B.: sudo bash $0 /media/usb/budget-sicherung"
[[ -f $DATA_DIR/budgetplaner.db ]] || die "Keine Datenbank unter $DATA_DIR gefunden."
mkdir -p "$TARGET"

STAMP=$(date +%Y%m%d-%H%M%S)
WORK=$(mktemp -d /tmp/budgetplaner-backup.XXXXXX)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/data" "$WORK/config"
chmod 755 "$WORK"
chown "$APP_USER" "$WORK/data"

say "Datenbank sichern (im laufenden Betrieb)"
# Als Dienstnutzer: so entstehen keine root-eigenen -wal/-shm-Dateien, die der Dienst nicht mehr öffnen könnte.
runuser -u "$APP_USER" -- sqlite3 "$DATA_DIR/budgetplaner.db" ".timeout 10000" "VACUUM INTO '$WORK/data/budgetplaner.db'"
if [[ -d $DATA_DIR/keys ]]; then cp -a "$DATA_DIR/keys" "$WORK/data/"; fi
cp -a "$ENV_FILE" "$WORK/config/"
git_repo log -1 --format='%H %cd' --date=iso > "$WORK/VERSION" 2>/dev/null || true

ARCHIVE="$TARGET/budgetplaner-$STAMP.tar.gz"
( umask 077 && tar -czf "$ARCHIVE" -C "$WORK" . )
info "Archiv: $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1))"

# Nur die neuesten $KEEP Archive behalten.
ls -1t "$TARGET"/budgetplaner-*.tar.gz 2>/dev/null | tail -n +$((KEEP + 1)) | xargs -r rm -f
info "Es werden die neuesten $KEEP Archive aufbewahrt (KEEP=… zum Ändern)."
warn "Das Archiv enthält die Schlüssel und das SMTP-Passwort – sicher aufbewahren, nicht weitergeben."
