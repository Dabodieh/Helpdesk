#!/usr/bin/env bash
# Monitoring probe for the hourly backup: exit 0 only when the last run succeeded AND finished within BACKUP_MAX_AGE_MINUTES (default 120 = two missed hours).
# Wire it to whatever already watches the host (cron + mail, a monitoring agent). A missing or unreadable status file is a failure.
#
#   BACKUP_STATUS_FILE=/mnt/offhost-staging/last-backup.json scripts/ops/check-backup-freshness.sh
set -euo pipefail

status="${BACKUP_STATUS_FILE:?path of last-backup.json}"
max="${BACKUP_MAX_AGE_MINUTES:-120}"
[ -s "$status" ] || { echo "STALE: no backup status file at $status" >&2; exit 1; }
text="$(cat "$status")"
case "$text" in *'"status":"ok"'*) ;; *) echo "FAILED: last backup run did not succeed: $text" >&2; exit 1;; esac
finished="$(printf '%s' "$text" | sed -n 's/.*"finishedAt":"\([^"]*\)".*/\1/p')"
[ -n "$finished" ] || { echo "STALE: status file has no finishedAt" >&2; exit 1; }
age=$(( ( $(date -u +%s) - $(date -u -d "$finished" +%s) ) / 60 ))
[ "$age" -le "$max" ] || { echo "STALE: last good backup finished $age minutes ago (limit $max)" >&2; exit 1; }
echo "backup fresh: finished $age minutes ago"
