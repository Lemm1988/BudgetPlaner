# Gemeinsame Einstellungen und Hilfsfunktionen für install.sh, update.sh, backup.sh, restore.sh, tailscale.sh.
# Wird per "source" eingebunden, nicht direkt ausführen.

APP_USER=budgetplaner
APP_DIR=/opt/budgetplaner
DATA_DIR=/var/lib/budgetplaner
CONF_DIR=/etc/budgetplaner
ENV_FILE=$CONF_DIR/budgetplaner.env
DOTNET_DIR=/opt/dotnet
SERVICE=budgetplaner
UNIT_FILE=/etc/systemd/system/$SERVICE.service
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

say()  { printf '\n\033[1;32m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
warn() { printf '\033[1;33m!!  %s\033[0m\n' "$*"; }
die()  { printf '\033[1;31mFEHLER: %s\033[0m\n' "$*" >&2; exit 1; }

require_root() {
  [[ $EUID -eq 0 ]] || die "Bitte mit sudo ausführen, z. B.: sudo bash $0 $*"
}

# Laufzeit-Kennung für dotnet publish aus der Prozessorarchitektur.
detect_rid() {
  case "$(uname -m)" in
    aarch64|arm64) echo linux-arm64 ;;
    armv7l) echo linux-arm ;;
    x86_64) echo linux-x64 ;;
    *) die "Prozessorarchitektur $(uname -m) wird von .NET 10 nicht unterstützt (Raspberry Pi 3/4/5 mit 64-Bit-System empfohlen)." ;;
  esac
}

# Port, auf dem der Dienst lauscht (aus der installierten systemd-Unit, sonst 5000).
installed_port() {
  local port
  port=$(grep -oE '127\.0\.0\.1:[0-9]+' "$UNIT_FILE" 2>/dev/null | head -1 | cut -d: -f2)
  echo "${port:-5000}"
}

# Wert aus der Konfigurationsdatei lesen (ohne Anführungszeichen).
get_env() {
  local line
  line=$(grep -E "^$1=" "$ENV_FILE" 2>/dev/null | tail -1) || true
  line=${line#*=}
  line=${line#\"}
  echo "${line%\"}"
}

# Wert in der Konfigurationsdatei setzen oder ergänzen.
set_env() {
  local key=$1 value=$2 tmp
  tmp=$(mktemp)
  if grep -qE "^#?\s*$key=" "$ENV_FILE"; then
    # Wert über ENVIRON übergeben: awk -v würde Backslashes im Wert (z. B. in Passwörtern) umdeuten.
    K="$key" V="$value" awk 'BEGIN { done = 0 }
      $0 ~ "^#?[[:space:]]*" ENVIRON["K"] "=" && !done { print ENVIRON["K"] "=" ENVIRON["V"]; done = 1; next } { print }' "$ENV_FILE" > "$tmp"
  else
    cat "$ENV_FILE" > "$tmp"
    printf '%s=%s\n' "$key" "$value" >> "$tmp"
  fi
  cat "$tmp" > "$ENV_FILE"
  rm -f "$tmp"
}

# git im Repository als dessen Besitzer ausführen (als root meldet git sonst „dubious ownership“).
git_repo() {
  sudo -u "${SUDO_USER:-root}" git -C "$REPO_DIR" "$@"
}

# dotnet als aufrufender Nutzer ausführen (nicht als root – sonst gehören bin/obj im Repository root).
run_dotnet() {
  local builder=${SUDO_USER:-root}
  local -a envs=(DOTNET_ROOT="$DOTNET_DIR" PATH="$DOTNET_DIR:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1)
  if [[ $builder != root ]]; then
    sudo -u "$builder" -H env "${envs[@]}" "$DOTNET_DIR/dotnet" "$@"
  else
    env "${envs[@]}" "$DOTNET_DIR/dotnet" "$@"
  fi
}

ensure_dotnet_sdk() {
  if [[ -x $DOTNET_DIR/dotnet ]] && "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
    info ".NET SDK $("$DOTNET_DIR/dotnet" --version) ist vorhanden."
    return
  fi
  say ".NET 10 SDK installieren nach $DOTNET_DIR (nur zum Bauen, ca. 1 GB, dauert einige Minuten)"
  local script
  script=$(mktemp)
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$script"
  bash "$script" --channel 10.0 --install-dir "$DOTNET_DIR" --no-path
  rm -f "$script"
  chmod -R a+rX "$DOTNET_DIR"
}

# App veröffentlichen (eigenständig, inkl. .NET-Laufzeit) nach $1.
publish_app() {
  local out=$1 rid
  rid=$(detect_rid)
  say "App bauen für $rid (beim ersten Mal 5–15 Minuten auf dem Pi)"
  local stage
  stage=$(mktemp -d /tmp/budgetplaner-publish.XXXXXX)
  chown "${SUDO_USER:-root}" "$stage"
  run_dotnet publish "$REPO_DIR/src/BudgetPlaner.Server/BudgetPlaner.Server.csproj" -c Release -r "$rid" \
    --self-contained true -p:DebugType=none -nologo -o "$stage/app"
  [[ -x $stage/app/BudgetPlaner.Server ]] || die "Build fehlgeschlagen: $stage/app/BudgetPlaner.Server fehlt."
  [[ -f $stage/app/wwwroot/index.html ]] || die "Build unvollständig: wwwroot/index.html fehlt."
  rm -rf "$out"
  mkdir -p "$(dirname "$out")"
  cp -a "$stage/app" "$out"
  rm -rf "$stage"
  chown -R root:root "$out"
  chmod -R u=rwX,go=rX "$out"
}

# Wartet, bis /health antwortet. Rückgabe 0 = gesund.
wait_healthy() {
  local port=${1:-$(installed_port)}
  for _ in $(seq 1 90); do
    if curl -fsS "http://127.0.0.1:$port/health" >/dev/null 2>&1; then return 0; fi
    if ! systemctl is-active --quiet "$SERVICE" && systemctl is-failed --quiet "$SERVICE"; then return 1; fi
    sleep 1
  done
  return 1
}

# Konsistente Kopie der Datenbank (Dienst muss gestoppt sein) nach $1.
copy_database() {
  local target=$1
  mkdir -p "$target"
  local f
  for f in "$DATA_DIR"/budgetplaner.db "$DATA_DIR"/budgetplaner.db-wal "$DATA_DIR"/budgetplaner.db-shm; do
    [[ -f $f ]] && cp -a "$f" "$target/"
  done
  return 0
}
