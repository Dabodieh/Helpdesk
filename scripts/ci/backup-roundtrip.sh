#!/usr/bin/env bash
# CI proof that the portable backup tooling works end to end against a DISPOSABLE migrated PostgreSQL container (never a live database):
# encrypted backup, verification, isolated restore into another disposable container, roles re-applied, comparison with the source, and one failure scenario.
#
#   PG_CONTAINER=ci-postgres DB_NAME=helpdesk DB_USER=helpdesk scripts/ci/backup-roundtrip.sh
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
: "${PG_CONTAINER:?name of the running, migrated PostgreSQL container}"
: "${DB_NAME:?database name}"
: "${DB_USER:?database user}"
export PG_CONTAINER DB_NAME DB_USER
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
export BACKUP_PASSPHRASE_FILE="$work/passphrase"
openssl rand -hex 32 > "$BACKUP_PASSPHRASE_FILE"; chmod 600 "$BACKUP_PASSPHRASE_FILE"
export BACKUP_DIR="$work/backups" BACKUP_NAME_PREFIX=ci

echo "== backup-all"
scripts/ops/backup-all.sh
file="$(ls "$BACKUP_DIR"/ci-*.dump.enc | head -1)"
grep -q '"status":"ok"' "$BACKUP_DIR/last-backup.json" || { echo "status file does not say ok" >&2; exit 1; }

echo "== isolated restore test (disposable PostgreSQL, roles, least privilege, comparison)"
RESTORE_TEST_RESULT="$work/result.json" ROLES_SQL=scripts/ops/prod-db-roles.sql COMPARE_VOLATILE_OK=1 scripts/ops/restore-test-isolated.sh "$file"
grep -q '"result":"ok"' "$work/result.json"
grep -q '"comparedWithLive":"yes"' "$work/result.json"

echo "== failure scenario: wrong passphrase must be rejected"
printf 'wrong' > "$work/wrong"
if BACKUP_PASSPHRASE_FILE="$work/wrong" scripts/ops/verify-backup.sh "$file" 2>/dev/null; then echo "verification accepted a wrong passphrase" >&2; exit 1; fi
echo "ok    rejected"

echo "== failure scenario: a damaged file must be rejected"
cp "$file" "$work/damaged.dump.enc"; cp "$file.sha256" "$work/damaged.dump.enc.sha256"
printf 'x' >> "$work/damaged.dump.enc"
if scripts/ops/verify-backup.sh "$work/damaged.dump.enc" 2>/dev/null; then echo "verification accepted a damaged file" >&2; exit 1; fi
echo "ok    rejected"
