#!/usr/bin/env bash
# The scheduled backup (run hourly, decision D3: RPO 1 hour): database backup, verification of the new file, optional object-store copy, then a status file.
# Any failure is reported three ways: a non-zero exit (systemd/cron mark the run failed), a line on stderr, and "status":"failed" in the status file. If
# BACKUP_ALERT_COMMAND is set it is run with the failure message as its only argument (mail, chat webhook, pager: operator's choice).
#
#   BACKUP_PASSPHRASE_FILE=/secure/backup.pass BACKUP_DIR=/mnt/offhost-staging scripts/ops/backup-all.sh
#
# Environment: everything backup-database.sh / verify-backup.sh read; BACKUP_STATUS_FILE (default $BACKUP_DIR/last-backup.json); and, only to include the
# object store, the variables backup-objects.sh needs (set OBJECT_BACKUP_DEST to turn it on). Only one run at a time (a second concurrent run exits quietly).
set -uo pipefail

: "${BACKUP_PASSPHRASE_FILE:?path of a file holding the encryption passphrase}"
: "${BACKUP_DIR:?directory to write the backup to}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$BACKUP_DIR"
STATUS_FILE="${BACKUP_STATUS_FILE:-$BACKUP_DIR/last-backup.json}"

lock="$BACKUP_DIR/.backup.lock"
if ! mkdir "$lock" 2>/dev/null; then
  if [ -f "$lock/pid" ] && kill -0 "$(cat "$lock/pid")" 2>/dev/null; then echo "another backup is running; skipping" >&2; exit 0; fi
  rm -rf "$lock"; mkdir "$lock" || { echo "cannot take the backup lock" >&2; exit 1; }
fi
echo $$ > "$lock/pid"
trap 'rm -rf "$lock"' EXIT

started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
file=""; objects="skipped"; problem=""

fail() {
  problem="$1"
  echo "BACKUP FAILED: $problem" >&2
  printf '{"status":"failed","startedAt":"%s","finishedAt":"%s","error":"%s"}\n' "$started" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${problem//\"/\'}" > "$STATUS_FILE.tmp" && mv "$STATUS_FILE.tmp" "$STATUS_FILE"
  if [ -n "${BACKUP_ALERT_COMMAND:-}" ]; then "$BACKUP_ALERT_COMMAND" "helpdesk backup failed: $problem" || echo "alert command failed" >&2; fi
  exit 1
}

out="$("$here/backup-database.sh" 2>&1)" || fail "database backup: $(printf '%s' "$out" | tail -1)"
file="$(printf '%s\n' "$out" | sed -n 's/^wrote //p' | tail -1)"
[ -n "$file" ] || fail "database backup did not report a file"
v="$("$here/verify-backup.sh" "$file" 2>&1)" || fail "verification: $(printf '%s' "$v" | tail -1)"

if [ -n "${OBJECT_BACKUP_DEST:-}" ]; then
  o="$("$here/backup-objects.sh" 2>&1)" || fail "object store: $(printf '%s' "$o" | tail -1)"
  objects="ok"
fi

size="$(stat -c %s "$file" 2>/dev/null || stat -f %z "$file")"
printf '{"status":"ok","startedAt":"%s","finishedAt":"%s","file":"%s","bytes":%s,"verified":true,"objects":"%s"}\n' \
  "$started" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$(basename "$file")" "$size" "$objects" > "$STATUS_FILE.tmp" && mv "$STATUS_FILE.tmp" "$STATUS_FILE"
echo "backup ok: $(basename "$file") (verified, objects: $objects)"
