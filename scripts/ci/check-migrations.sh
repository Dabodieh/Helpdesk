#!/usr/bin/env bash
# CI migration validation against a DISPOSABLE PostgreSQL (never a live database):
#   1. applies every module migration to an empty database through the product's own `migrate` command,
#   2. checks each module's history row count equals the migration files in the source (nothing silently skipped or missing),
#   3. runs `migrate` a second time (must be a no-op),
#   4. asserts the EF models have no pending changes compared with the migrations (schema drift).
#
#   DB_CONNECTION="Host=localhost;Port=5432;Database=helpdesk;Username=helpdesk;Password=..." scripts/ci/check-migrations.sh
# PSQL_CMD (default: psql) lets a caller run psql through `docker exec`. The connection string is never printed.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
: "${DB_CONNECTION:?connection string of the disposable database}"
PSQL_CMD="${PSQL_CMD:-psql}"
export ASPNETCORE_ENVIRONMENT=Development Database__ConnectionString="$DB_CONNECTION"

run_migrate() { dotnet run --no-build -c Release --project src/Helpdesk.Host -- migrate 2>&1 | grep -E "All module migrations applied|FTL|Unhandled" ; }

echo "== apply all migrations to an empty database"
run_migrate | grep -q "All module migrations applied" || { echo "migrate did not report success" >&2; exit 1; }

echo "== history rows equal the migration files, per module"
fail=0
for entry in audit:Audit identity:Identity organisation:Organisation mail:Mail ticketing:Ticketing attachments:Attachments knowledge:Knowledge; do
  schema="${entry%%:*}"; module="${entry##*:}"
  files="$(find "src/Helpdesk.Modules.$module/Persistence/Migrations" -maxdepth 1 -name '*.cs' ! -name '*.Designer.cs' ! -name '*Snapshot.cs' | wc -l | tr -d ' ')"
  rows="$($PSQL_CMD -At -c "select count(*) from $schema.__ef_migrations_history" | tr -d '\r')"
  if [ "$files" = "$rows" ]; then echo "ok    $schema: $rows migrations"; else echo "FAIL  $schema: source has $files migration(s), database history has $rows" >&2; fail=1; fi
done
[ "$fail" = 0 ] || exit 1

echo "== second run is a no-op"
before="$($PSQL_CMD -At -c "select count(*) from information_schema.tables where table_schema not in ('pg_catalog','information_schema')" | tr -d '\r')"
run_migrate | grep -q "All module migrations applied" || { echo "second migrate failed" >&2; exit 1; }
after="$($PSQL_CMD -At -c "select count(*) from information_schema.tables where table_schema not in ('pg_catalog','information_schema')" | tr -d '\r')"
[ "$before" = "$after" ] || { echo "second migrate changed the schema ($before -> $after tables)" >&2; exit 1; }
echo "ok    $after tables, unchanged"

echo "== EF models match the migrations"
dotnet tool restore >/dev/null
for module in Audit Identity Organisation Mail Ticketing Attachments Knowledge; do
  out="$(dotnet ef migrations has-pending-model-changes --no-build --configuration Release --project "src/Helpdesk.Modules.$module" --startup-project src/Helpdesk.Host --context "${module}DbContext" 2>&1 | tail -1)"
  case "$out" in
    *"No changes"*) echo "ok    $module" ;;
    *) echo "FAIL  $module: $out" >&2; fail=1 ;;
  esac
done
exit "$fail"
