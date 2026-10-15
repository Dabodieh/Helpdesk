#!/usr/bin/env bash
# Verifies an encrypted Helpdesk database backup WITHOUT restoring it: checksum, decryption and a readable pg_dump table of contents that contains data for the
# expected schemas. Exit 0 = usable, non-zero = do not trust this file. Writes nothing and never touches a live database.
#
#   BACKUP_PASSPHRASE_FILE=/secure/backup.pass scripts/ops/verify-backup.sh helpdesk-20261008T100000Z.dump.enc
#
# Environment: PG_CONTAINER or COMPOSE_FILE/ENV_FILE/PG_SERVICE (only used to run pg_restore --list); VERIFY_EXPECT_SCHEMAS (default "identity ticketing").
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$here/lib-backup.sh"

backup="${1:?usage: verify-backup.sh <backup.dump.enc>}"
require_passphrase
EXPECT="${VERIFY_EXPECT_SCHEMAS:-identity ticketing}"

fail() { echo "backup verification FAILED: $*" >&2; exit 1; }

[ -s "$backup" ] || fail "file is missing or empty"
[ -f "$backup.sha256" ] || fail "checksum file $backup.sha256 is missing"
sha_ok "$backup" || fail "checksum mismatch (file damaged or altered)"

# Decrypt and let pg_restore read the archive's table of contents (no database is involved, nothing is written).
toc="$(dec < "$backup" 2>/dev/null | pg_exec pg_restore --list 2>/dev/null)" || fail "cannot decrypt or read the archive (wrong passphrase or damaged file)"

tables="$(printf '%s\n' "$toc" | grep -c ' TABLE DATA ' || true)"
[ "$tables" -gt 0 ] || fail "archive lists no table data"
for schema in $EXPECT; do
  printf '%s\n' "$toc" | grep -q " TABLE DATA $schema " || fail "no table data for schema '$schema'"
done
# A portable backup must not carry ownership or grants (restore would need the original roles).
if printf '%s\n' "$toc" | grep -q -E ' ACL | OWNER '; then fail "archive contains ACL/OWNER entries (taken without --no-owner --no-acl)"; fi
echo "backup verified: $backup ($tables tables with data)"
