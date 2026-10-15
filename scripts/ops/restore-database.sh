#!/usr/bin/env bash
# Restore an encrypted Helpdesk backup into a database (restore drills and disaster recovery; decisions D3 and D5: rollback is restore, not down-migrations).
#
#   BACKUP_PASSPHRASE_FILE=/secure/backup.pass scripts/ops/restore-database.sh helpdesk-20261008T100000Z.dump.enc helpdesk_restore
#   PG_CONTAINER=helpdesk-postgres-prod-1 ... restore-database.sh <backup> <new_database_name>      # local Docker host
#
# The target database is created if missing and MUST be empty: an existing non-empty database is refused. To replace production, stop the application first,
# restore into a NEW database name, check it, then point Database__ConnectionString at it (or rename) - never restore over live data.
# After a restore the application role must be re-created: scripts/ops/prod-db-roles.sql (the archive carries no ownership or grants).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$here/lib-backup.sh"

backup="${1:?usage: restore-database.sh <backup.dump.enc> <target-database>}"
target="${2:?usage: restore-database.sh <backup.dump.enc> <target-database>}"
require_passphrase
[ -n "$DB_USER" ] || { echo "cannot determine the database user" >&2; exit 1; }

case "$target" in *[!a-zA-Z0-9_]*|"") echo "target database name must be [a-zA-Z0-9_]+" >&2; exit 1;; esac
[ -f "$backup.sha256" ] || { echo "checksum file $backup.sha256 is missing; refusing to restore an unverified file" >&2; exit 1; }
sha_ok "$backup" || { echo "checksum mismatch; refusing to restore" >&2; exit 1; }

exists="$(pg_exec psql -U "$DB_USER" -d postgres -X -tAc "SELECT 1 FROM pg_database WHERE datname='$target'" | tr -d '\r')"
if [ "$exists" = "1" ]; then
  objects="$(pg_exec psql -U "$DB_USER" -d "$target" -X -tAc "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema')" | tr -d '\r')"
  [ "$objects" = "0" ] || { echo "database $target is not empty; refusing to restore over it" >&2; exit 1; }
else
  pg_exec psql -U "$DB_USER" -d postgres -X -c "CREATE DATABASE $target" >/dev/null
fi

dec < "$backup" | pg_exec pg_restore -U "$DB_USER" -d "$target" --no-owner --no-acl --exit-on-error
echo "restored $backup into $target"
