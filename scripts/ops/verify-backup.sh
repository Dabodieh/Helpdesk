#!/usr/bin/env bash
# Verifies an encrypted Helpdesk database backup WITHOUT restoring it: checksum, decryption and a readable pg_dump table of contents that contains data for the
# expected schemas. Exit 0 = usable, non-zero = do not trust this file. Writes nothing and never touches a live database.
#
#   BACKUP_PASSPHRASE_FILE=/secure/backup.pass scripts/ops/verify-backup.sh helpdesk-20261008T100000Z.dump.enc
#
# Environment: COMPOSE_FILE, ENV_FILE, PG_SERVICE (as the other ops scripts); VERIFY_EXPECT_SCHEMAS (default "identity ticketing").
set -euo pipefail

backup="${1:?usage: verify-backup.sh <backup.dump.enc>}"
: "${BACKUP_PASSPHRASE_FILE:?path of the file holding the encryption passphrase}"
COMPOSE_FILE="${COMPOSE_FILE:-docker/production/compose.yml}"
ENV_FILE="${ENV_FILE:-docker/production/.env}"
PG_SERVICE="${PG_SERVICE:-postgres}"
EXPECT="${VERIFY_EXPECT_SCHEMAS:-identity ticketing}"

fail() { echo "backup verification FAILED: $*" >&2; exit 1; }

[ -s "$backup" ] || fail "file is missing or empty"
[ -f "$backup.sha256" ] || fail "checksum file $backup.sha256 is missing"
( cd "$(dirname "$backup")" && sha256sum -c --status "$(basename "$backup").sha256" ) || fail "checksum mismatch (file damaged or altered)"

# Decrypt and let pg_restore read the archive's table of contents (no database is involved, nothing is written).
toc="$(openssl enc -d -aes-256-cbc -pbkdf2 -pass "file:$BACKUP_PASSPHRASE_FILE" -in "$backup" 2>/dev/null \
  | docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T "$PG_SERVICE" pg_restore --list 2>/dev/null)" || fail "cannot decrypt or read the archive (wrong passphrase or damaged file)"

tables="$(printf '%s\n' "$toc" | grep -c ' TABLE DATA ' || true)"
[ "$tables" -gt 0 ] || fail "archive lists no table data"
for schema in $EXPECT; do
  printf '%s\n' "$toc" | grep -q " TABLE DATA $schema " || fail "no table data for schema '$schema'"
done
echo "backup verified: $backup ($tables tables with data)"
