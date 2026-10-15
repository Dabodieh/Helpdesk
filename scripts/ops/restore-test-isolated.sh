#!/usr/bin/env bash
# Fully isolated restore test: restores a backup into a DISPOSABLE PostgreSQL container (no network, no volume, removed at the end), re-applies the Production database
# roles, checks the least-privilege role, and compares the restored data with the live database (read-only queries). It never writes to, connects with write
# intent to, or restarts anything that is live.
#
#   BACKUP_PASSPHRASE_FILE=/secure/backup.pass PG_CONTAINER=helpdesk-postgres-prod-1 scripts/ops/restore-test-isolated.sh /backups/helpdesk-prod-20261015T100902Z.dump.enc
#
# Environment: PG_CONTAINER (live source for the comparison; leave unset to skip the comparison), DISPOSABLE_IMAGE (default: the live server's image, else postgres:18.6),
# ROLES_SQL (default scripts/ops/prod-db-roles.sql), RESTORE_TEST_RESULT (JSON result path, default ./restore-test-result.json), COMPARE_VOLATILE_OK=1 (default: differences in
# tables that change continuously - hangfire.*, mail.mailbox_sync, mail.notifications - are reported but do not fail the test; set 0 to make them fail).
# Exit 0 = restore usable and consistent; 1 = failed (reason on stderr). No credential or data content is printed.
set -euo pipefail
umask 077
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$here/lib-backup.sh"

backup="${1:?usage: restore-test-isolated.sh <backup.dump.enc>}"
require_passphrase
ROLES_SQL="${ROLES_SQL:-$here/prod-db-roles.sql}"
RESULT="${RESTORE_TEST_RESULT:-./restore-test-result.json}"
[ -f "$ROLES_SQL" ] || { echo "roles script not found: $ROLES_SQL" >&2; exit 1; }

image="${DISPOSABLE_IMAGE:-}"
if [ -z "$image" ]; then
  if [ -n "$PG_CONTAINER" ]; then image="$(docker inspect --format '{{.Config.Image}}' "$PG_CONTAINER" 2>/dev/null || true)"; fi
  image="${image:-postgres:18.6}"
fi
name="helpdesk-restore-test-$(stamp_now)"
admin="restore_admin"
rand() { openssl rand -hex 16; }
admin_pw="$(rand)"; app_pw="$(rand)"
app_user="${APP_DB_USER:-helpdesk_app}"
t0="$(date -u +%s)"
result="failed"; detail=""; volatile_diff=0; compared="no"

cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT
write_result() {
  printf '{"result":"%s","backup":"%s","image":"%s","seconds":%s,"comparedWithLive":"%s","volatileTableDifferences":%s,"detail":"%s","testedAt":"%s"}\n' \
    "$result" "$(basename "$backup")" "$image" "$(( $(date -u +%s) - t0 ))" "$compared" "$volatile_diff" "$(printf '%s' "$detail" | tr -d '\r"\\' | cut -c1-300)" "$(utc_now)" > "$RESULT"
  cat "$RESULT"
}
bad() { detail="$1"; write_result; echo "ISOLATED RESTORE TEST FAILED: $1" >&2; exit 1; }
rpsql() { docker exec -i "$name" psql -U "$admin" -d restored -X -At -F '|' -v ON_ERROR_STOP=1 "$@"; }

"$here/verify-backup.sh" "$backup" >/dev/null 2>&1 || bad "backup did not verify (checksum, decryption or archive)"

docker run -d --name "$name" --network none --tmpfs /var/lib/postgresql:rw \
  -e POSTGRES_USER="$admin" -e POSTGRES_PASSWORD="$admin_pw" -e POSTGRES_DB=restored "$image" >/dev/null || bad "cannot start the disposable PostgreSQL container"
ok=0
for _ in $(seq 1 90); do
  if docker exec "$name" psql -U "$admin" -d restored -X -At -c 'select 1' >/dev/null 2>&1; then sleep 3; docker exec "$name" psql -U "$admin" -d restored -X -At -c 'select 1' >/dev/null 2>&1 && { ok=1; break; }; fi
  sleep 1
done
[ "$ok" = 1 ] || bad "disposable PostgreSQL did not become ready"

dec < "$backup" | docker exec -i "$name" pg_restore -U "$admin" -d restored --no-owner --no-acl --exit-on-error >/dev/null 2>"$RESULT.err" \
  || bad "pg_restore failed: $(tail -1 "$RESULT.err" 2>/dev/null)"
rm -f "$RESULT.err"

# Roles: the archive carries no ownership or grants, so the application role is created exactly as in production.
docker exec -i "$name" psql -U "$admin" -d restored -X -q -v ON_ERROR_STOP=1 -v app_user="$app_user" -v app_password="$app_pw" < "$ROLES_SQL" >/dev/null 2>&1 \
  || bad "applying the roles script failed"
asapp() { docker exec -e PGPASSWORD="$app_pw" "$name" psql -h 127.0.0.1 -U "$app_user" -d restored -X -At "$@"; }
asapp -c 'select count(*) from identity.users' >/dev/null 2>&1 || bad "application role cannot read the restored data"
if asapp -c 'create table ticketing.restore_test_probe(x int)' >/dev/null 2>&1; then bad "application role can create objects (least privilege broken)"; fi
if asapp -c 'truncate identity.users' >/dev/null 2>&1; then bad "application role can truncate tables (least privilege broken)"; fi

# Comparison with the live database (read-only): row counts and a content checksum per table, plus the migration history.
cat > "${RESULT}.sums.sql" <<'SQL'
select table_schema || '.' || table_name,
       (xpath('/row/c/text()', query_to_xml(format('select count(*) as c from %I.%I', table_schema, table_name), false, true, '')))[1]::text,
       (xpath('/row/m/text()', query_to_xml(format('select md5(coalesce(string_agg(t::text, $q$|$q$ order by t::text), $q$$q$)) as m from %I.%I t', table_schema, table_name), false, true, '')))[1]::text
from information_schema.tables where table_schema not in ('pg_catalog', 'information_schema') and table_type = 'BASE TABLE' order by 1;
SQL
tables_restored="$(rpsql -c "select count(*) from information_schema.tables where table_schema not in ('pg_catalog','information_schema') and table_type='BASE TABLE'" | tr -d '\r')"
[ "${tables_restored:-0}" -gt 0 ] || bad "restored database has no tables"
rpsql < "${RESULT}.sums.sql" | tr -d '\r' > "${RESULT}.restored.txt"

if [ -n "$PG_CONTAINER" ] && docker exec "$PG_CONTAINER" pg_isready -U "$DB_USER" -d "$DB_NAME" >/dev/null 2>&1; then
  compared="yes"
  pg_exec psql -U "$DB_USER" -d "$DB_NAME" -X -At -F '|' -v ON_ERROR_STOP=1 < "${RESULT}.sums.sql" | tr -d '\r' > "${RESULT}.live.txt" || bad "cannot read the live database for comparison"
  stable_diff="$(diff <(grep -v -E '^(hangfire\.|mail\.mailbox_sync\||mail\.notifications\|)' "${RESULT}.live.txt") <(grep -v -E '^(hangfire\.|mail\.mailbox_sync\||mail\.notifications\|)' "${RESULT}.restored.txt") | grep -c '^[<>]' || true)"
  volatile_diff="$(diff <(grep -E '^(hangfire\.|mail\.mailbox_sync\||mail\.notifications\|)' "${RESULT}.live.txt") <(grep -E '^(hangfire\.|mail\.mailbox_sync\||mail\.notifications\|)' "${RESULT}.restored.txt") | grep -c '^[<>]' || true)"
  hist_diff="$(diff <(grep -E '__ef_migrations_history\|' "${RESULT}.live.txt") <(grep -E '__ef_migrations_history\|' "${RESULT}.restored.txt") | grep -c '^[<>]' || true)"
  [ "$hist_diff" = "0" ] || bad "migration history differs from the live database"
  if [ "$stable_diff" != "0" ]; then
    names="$(diff <(grep -v -E '^(hangfire\.|mail\.mailbox_sync\||mail\.notifications\|)' "${RESULT}.live.txt" | cut -d'|' -f1,2) <(grep -v -E '^(hangfire\.|mail\.mailbox_sync\||mail\.notifications\|)' "${RESULT}.restored.txt" | cut -d'|' -f1,2) | grep '^[<>]' | cut -d'|' -f1 | sort -u | head -5 | tr '\n' ' ')"
    bad "data differs from the live database in: ${names:-content checksums} (changes made after the backup? take a new backup and test again)"
  fi
  if [ "${COMPARE_VOLATILE_OK:-1}" != "1" ] && [ "$volatile_diff" != "0" ]; then bad "continuously changing tables differ from the live database"; fi
fi
rm -f "${RESULT}.sums.sql" "${RESULT}.restored.txt" "${RESULT}.live.txt"

result="ok"; detail="$tables_restored tables restored, roles applied, least privilege verified"
[ "$compared" = "yes" ] && detail="$detail, identical to live (stable tables + migration history)"
write_result
