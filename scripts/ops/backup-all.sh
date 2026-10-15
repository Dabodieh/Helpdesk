#!/usr/bin/env bash
# The scheduled backup: database backup, verification of the new file, optional object-store and configuration bundles, optional verified copies to further
# destinations, then a status file. Portable: bash + docker + openssl + coreutils (Linux, Unraid, Docker Desktop through Git Bash).
#
#   BACKUP_PASSPHRASE_FILE=/secure/backup.pass BACKUP_DIR=/mnt/backups/helpdesk PG_CONTAINER=helpdesk-postgres-prod-1 scripts/ops/backup-all.sh
#
# Environment (all optional unless marked):
#   BACKUP_PASSPHRASE_FILE (required)  file holding the passphrase; BACKUP_DIR (required)  where the files are written.
#   PG_CONTAINER                       PostgreSQL container; otherwise the compose variables of lib-backup.sh. DB_NAME defaults to helpdesk_prod with PG_CONTAINER.
#   BACKUP_NAME_PREFIX                 file name prefix (default helpdesk).
#   OBJECT_VOLUME | OBJECT_BACKUP_DEST include the object store (see backup-objects.sh).
#   CONFIG_PATHS                       semicolon-separated files/folders for the encrypted configuration bundle (see backup-config.sh).
#   BACKUP_COPY_DIRS                   semicolon-separated extra destination directories (second disk, NAS mount, Unraid share). Every new file is copied and its
#                                      checksum re-verified there; a failed copy fails the run. Nothing is ever deleted at a destination.
#   BACKUP_STATUS_FILE                 default $BACKUP_DIR/last-backup.json.   BACKUP_ALERT_COMMAND  run with the failure message as its only argument.
#
# Exit codes: 0 ok; 1 failed (reason on stderr and in the status file; no passphrase or credential is ever included); 75 another backup is already running.
# Only one run at a time (lock directory with the owner's pid; a lock whose process is gone is recovered).
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$here/lib-backup.sh"

: "${BACKUP_PASSPHRASE_FILE:?path of a file holding the encryption passphrase}"
: "${BACKUP_DIR:?directory to write the backup to}"
mkdir -p "$BACKUP_DIR"
STATUS_FILE="${BACKUP_STATUS_FILE:-$BACKUP_DIR/last-backup.json}"

lock="$BACKUP_DIR/.backup.lock"
# Overlap protection. A lock is only taken over when its owner is provably gone OR it is older than BACKUP_LOCK_STALE_MINUTES (default 180): process ids are not
# comparable across shells on every platform (Git Bash), so an unprovable owner is treated as running until the lock is old enough to be a crashed run.
if ! mkdir "$lock" 2>/dev/null; then
  stale="${BACKUP_LOCK_STALE_MINUTES:-180}"
  if [ -f "$lock/pid" ] && kill -0 "$(cat "$lock/pid")" 2>/dev/null; then
    echo "another backup is running (pid $(cat "$lock/pid")); exiting without work" >&2; exit 75
  fi
  if [ "${BACKUP_FORCE_UNLOCK:-0}" != "1" ] && [ -n "$(find "$lock" -maxdepth 0 -mmin "-$stale" 2>/dev/null)" ]; then
    echo "a backup lock younger than $stale minutes exists (another run, or a crashed run); exiting without work. BACKUP_FORCE_UNLOCK=1 overrides." >&2; exit 75
  fi
  rm -rf "$lock"; mkdir "$lock" || { echo "cannot take the backup lock" >&2; exit 1; }
fi
echo $$ > "$lock/pid"
trap 'rm -rf "$lock"' EXIT

started="$(utc_now)"
files=()

json_escape() { printf '%s' "$1" | tr '\\' '/' | tr -d '\r' | sed 's/"/'"'"'/g' | tr '\n' ' ' | cut -c1-400; }

fail() {
  local problem="$1"
  echo "BACKUP FAILED: $problem" >&2
  printf '{"status":"failed","startedAt":"%s","finishedAt":"%s","error":"%s"}\n' "$started" "$(utc_now)" "$(json_escape "$problem")" > "$STATUS_FILE.tmp" \
    && mv "$STATUS_FILE.tmp" "$STATUS_FILE"
  if [ -n "${BACKUP_ALERT_COMMAND:-}" ]; then "$BACKUP_ALERT_COMMAND" "helpdesk backup failed: $problem" || echo "alert command failed" >&2; fi
  exit 1
}

# --- database ---
out="$("$here/backup-database.sh" 2>&1)" || fail "database backup: $(printf '%s' "$out" | tail -1)"
db_file="$(printf '%s\n' "$out" | sed -n 's/^wrote //p' | tail -1)"
[ -n "$db_file" ] && [ -s "$db_file" ] || fail "database backup did not report a file"
files+=("$db_file" "$db_file.sha256" "$db_file.meta.json")
v="$("$here/verify-backup.sh" "$db_file" 2>&1)" || fail "verification: $(printf '%s' "$v" | tail -1)"

# --- objects ---
objects="skipped"
if [ -n "${OBJECT_VOLUME:-}" ] || [ -n "${OBJECT_BACKUP_DEST:-}" ]; then
  o="$("$here/backup-objects.sh" 2>&1)" || fail "object store: $(printf '%s' "$o" | tail -1)"
  objects="ok"
  obj_file="$(printf '%s\n' "$o" | sed -n 's/^wrote \([^ ]*\) .*/\1/p' | tail -1)"
  if [ -n "$obj_file" ] && [ -s "$obj_file" ]; then files+=("$obj_file" "$obj_file.sha256"); fi
fi

# --- configuration bundle ---
config="skipped"
if [ -n "${CONFIG_PATHS:-}" ]; then
  c="$("$here/backup-config.sh" 2>&1)" || fail "configuration bundle: $(printf '%s' "$c" | tail -1)"
  config="ok"
  cfg_file="$(printf '%s\n' "$c" | sed -n 's/^wrote \([^ ]*\) .*/\1/p' | tail -1)"
  if [ -n "$cfg_file" ] && [ -s "$cfg_file" ]; then files+=("$cfg_file" "$cfg_file.sha256"); fi
fi

# --- verified copies ---
copies=0
if [ -n "${BACKUP_COPY_DIRS:-}" ]; then
  IFS=';' read -r -a dests <<< "$BACKUP_COPY_DIRS"
  for d in "${dests[@]}"; do
    [ -n "$d" ] || continue
    mkdir -p "$d" 2>/dev/null || fail "copy destination not available: $(basename "$d")"
    for f in "${files[@]}"; do
      [ -e "$d/$(basename "$f")" ] && continue # never overwrite
      cp "$f" "$d/" 2>/dev/null || fail "copy to $(basename "$d") failed for $(basename "$f")"
    done
    for f in "${files[@]}"; do
      case "$f" in *.sha256|*.meta.json) continue;; esac
      sha_ok "$d/$(basename "$f")" || fail "copy in $(basename "$d") does not match its checksum: $(basename "$f")"
    done
    copies=$((copies + 1))
  done
fi

size="$(file_size "$db_file")"
printf '{"status":"ok","startedAt":"%s","finishedAt":"%s","file":"%s","bytes":%s,"verified":true,"objects":"%s","config":"%s","copies":%s}\n' \
  "$started" "$(utc_now)" "$(basename "$db_file")" "$size" "$objects" "$config" "$copies" > "$STATUS_FILE.tmp" && mv "$STATUS_FILE.tmp" "$STATUS_FILE"
echo "backup ok: $(basename "$db_file") (verified, objects: $objects, config: $config, extra copies: $copies)"
