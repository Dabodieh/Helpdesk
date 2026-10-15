#!/usr/bin/env bash
# Safe restore test (the quarterly drill, or after any change to the backup chain): restores a backup into a throw-away database, checks that it is usable, measures
# the time against the RTO and the backup's age against the RPO, writes the result, and drops the scratch database. It can NEVER touch the live database: the target
# name is generated ("restore_test_<UTC stamp>"), restore-database.sh refuses a non-empty target, and only a database with that prefix is ever dropped.
#
#   BACKUP_PASSPHRASE_FILE=/secure/backup.pass scripts/ops/restore-test.sh /mnt/offhost-staging/helpdesk-20261008T100000Z.dump.enc
#
# NOTE: this restores into a scratch database ON THE SAME SERVER. For a fully isolated test (separate disposable server, roles, comparison) use
# restore-test-isolated.sh.
# Environment: as restore-database.sh; RESTORE_TEST_RESULT (default ./restore-test-result.json; record the outcome in BACKUP_AND_RESTORE.md);
# RESTORE_TEST_KEEP=1 keeps the scratch database for inspection; RESTORE_TEST_EXPECT_SCHEMAS (default "identity ticketing"); RTO_MINUTES (240), RPO_MINUTES (60).
set -euo pipefail

backup="${1:?usage: restore-test.sh <backup.dump.enc>}"
: "${BACKUP_PASSPHRASE_FILE:?path of the file holding the encryption passphrase}"
RESULT="${RESTORE_TEST_RESULT:-./restore-test-result.json}"
RTO="${RTO_MINUTES:-240}"; RPO="${RPO_MINUTES:-60}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$here/lib-backup.sh"
psqlx() { pg_exec psql -U "$DB_USER" -X -tA "$@" | tr -d '\015'; }

scratch="restore_test_$(date -u +%Y%m%d%H%M%S)"
case "$scratch" in restore_test_[0-9]*) ;; *) echo "refusing: unexpected scratch name" >&2; exit 1;; esac
cleanup() {
  if [ "${RESTORE_TEST_KEEP:-0}" != "1" ]; then psqlx -d postgres -c "DROP DATABASE IF EXISTS $scratch" >/dev/null 2>&1 || echo "could not drop $scratch; drop it by hand" >&2; fi
}
trap cleanup EXIT

result="failed"; detail=""
write_result() {
  printf '{"result":"%s","backup":"%s","scratchDatabase":"%s","restoreSeconds":%s,"backupAgeMinutes":%s,"withinRto":%s,"withinRpo":%s,"detail":"%s","testedAt":"%s"}\n' \
    "$result" "$(basename "$backup")" "$scratch" "${secs:-0}" "${age:-0}" "${in_rto:-false}" "${in_rpo:-false}" "${detail//\"/\'}" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$RESULT"
  cat "$RESULT"
}
bad() { detail="$1"; write_result; echo "RESTORE TEST FAILED: $1" >&2; exit 1; }

"$here/verify-backup.sh" "$backup" >/dev/null 2>&1 || bad "backup did not verify"
start="$(date -u +%s)"
out="$("$here/restore-database.sh" "$backup" "$scratch" 2>&1)" || bad "restore: $(printf '%s' "$out" | tail -1)"
secs=$(( $(date -u +%s) - start ))

tables="$(psqlx -d "$scratch" -c "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema')")"
[ "${tables:-0}" -gt 0 ] || bad "restored database has no tables"
for schema in ${RESTORE_TEST_EXPECT_SCHEMAS:-identity ticketing}; do
  n="$(psqlx -d "$scratch" -c "SELECT count(*) FROM pg_tables WHERE schemaname = '$schema'")"
  [ "${n:-0}" -gt 0 ] || bad "schema '$schema' is missing or empty after restore"
done

stamp="$(basename "$backup" | sed -n 's/^.*-\([0-9]\{8\}T[0-9]\{6\}Z\)\.dump\.enc$/\1/p')"
age=0
if [ -n "$stamp" ]; then
  iso="${stamp:0:4}-${stamp:4:2}-${stamp:6:2}T${stamp:9:2}:${stamp:11:2}:${stamp:13:2}Z"
  age=$(( ( $(date -u +%s) - $(date -u -d "$iso" +%s) ) / 60 ))
fi
in_rto=false; [ $(( secs / 60 )) -le "$RTO" ] && in_rto=true
in_rpo=false; [ "$age" -le "$RPO" ] && in_rpo=true
result="ok"; detail="$tables tables restored; row counts not read"
write_result
