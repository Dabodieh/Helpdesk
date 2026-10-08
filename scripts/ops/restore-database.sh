#!/usr/bin/env bash
# Restore an encrypted Helpdesk backup into a database (restore drills and disaster recovery; decisions D3 and D5: rollback is restore, not down-migrations).
#
#   BACKUP_PASSPHRASE_FILE=/secure/backup.pass scripts/ops/restore-database.sh helpdesk-20261008T100000Z.dump.enc helpdesk_restore
#
# The target database is created if missing and MUST be empty: an existing non-empty database is refused. To replace production, stop the application first,
# restore into a NEW database name, check it, then point Database__ConnectionString at it (or rename) - never restore over live data.
set -euo pipefail

backup="${1:?usage: restore-database.sh <backup.dump.enc> <target-database>}"
target="${2:?usage: restore-database.sh <backup.dump.enc> <target-database>}"
: "${BACKUP_PASSPHRASE_FILE:?path of the file holding the encryption passphrase}"
COMPOSE_FILE="${COMPOSE_FILE:-docker/production/compose.yml}"
DB_NAME="${DB_NAME:-helpdesk}"; DB_USER="${DB_USER:-helpdesk}"; PG_SERVICE="${PG_SERVICE:-postgres}"
ENV_FILE="${ENV_FILE:-docker/production/.env}"
dc() { docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"; }

case "$target" in *[!a-zA-Z0-9_]*|"") echo "target database name must be [a-zA-Z0-9_]+" >&2; exit 1;; esac
[ -f "$backup.sha256" ] && ( cd "$(dirname "$backup")" && sha256sum -c "$(basename "$backup").sha256" )

exists="$(dc exec -T "$PG_SERVICE" psql -U "$DB_USER" -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$target'")"
if [ "$exists" = "1" ]; then
  objects="$(dc exec -T "$PG_SERVICE" psql -U "$DB_USER" -d "$target" -tAc "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema')")"
  [ "$objects" = "0" ] || { echo "database $target is not empty; refusing to restore over it" >&2; exit 1; }
else
  dc exec -T "$PG_SERVICE" psql -U "$DB_USER" -d postgres -c "CREATE DATABASE $target"
fi

openssl enc -d -aes-256-cbc -pbkdf2 -pass "file:$BACKUP_PASSPHRASE_FILE" -in "$backup" \
  | dc exec -T "$PG_SERVICE" pg_restore -U "$DB_USER" -d "$target" --no-owner --exit-on-error
echo "restored $backup into $target"
