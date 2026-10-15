#!/usr/bin/env bash
# Encrypted logical backup of the Helpdesk PostgreSQL database (decision D3: RPO 1 hour, encrypted, kept OFF the Helpdesk host).
#
#   BACKUP_PASSPHRASE_FILE=/secure/backup.pass BACKUP_DIR=/mnt/offhost-staging scripts/ops/backup-database.sh
#   # local Docker (Windows Docker Desktop through Git Bash, Unraid, plain Docker host):
#   PG_CONTAINER=helpdesk-postgres-prod-1 BACKUP_NAME_PREFIX=helpdesk-prod BACKUP_PASSPHRASE_FILE=... BACKUP_DIR=... scripts/ops/backup-database.sh
#
# Writes <BACKUP_DIR>/<prefix>-<UTC timestamp>.dump.enc, a .sha256 and a .meta.json (format description, no secrets) next to it. Always `pg_dump --format=custom
# --no-owner --no-acl`: ownership and grants are NOT in the archive, so it restores on any server; roles are re-applied afterwards with scripts/ops/prod-db-roles.sql.
# The dump is streamed straight into encryption: no plaintext copy of the data is ever written to disk. The passphrase file is never printed.
set -euo pipefail
umask 077 # backups and their checksums are readable by the backup user only
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib-backup.sh
. "$here/lib-backup.sh"

require_passphrase
: "${BACKUP_DIR:?directory to write the backup to}"
[ -n "$DB_USER" ] || { echo "cannot determine the database user (is the PostgreSQL container running?)" >&2; exit 1; }
mkdir -p "$BACKUP_DIR"

stamp="$(stamp_now)"
out="$BACKUP_DIR/$BACKUP_NAME_PREFIX-$stamp.dump.enc"
# A failed or interrupted run must not leave a partial file that looks like a backup.
cleanup() { local rc=$?; [ "$rc" -eq 0 ] || rm -f "$out" "$out.sha256" "$out.meta.json"; }
trap cleanup EXIT

pg_exec pg_dump -U "$DB_USER" -d "$DB_NAME" --format=custom --no-owner --no-acl | enc > "$out"

# An empty or truncated dump must never be mistaken for a backup.
[ "$(file_size "$out")" -gt 1024 ] || { echo "backup is implausibly small; removed" >&2; exit 1; }
server="$(pg_exec psql -U "$DB_USER" -d "$DB_NAME" -X -At -c 'show server_version' 2>/dev/null | head -1 | tr -d '\r' || true)"
printf '{"createdAt":"%s","database":"%s","pgDump":"--format=custom --no-owner --no-acl","cipher":"aes-256-cbc","kdf":"pbkdf2","iterations":%s,"salt":"openssl-enc","serverVersion":"%s","decrypt":"openssl enc -d -aes-256-cbc -pbkdf2 -iter %s -pass file:<passphrase-file> -in <this-file>"}\n' \
  "$(utc_now)" "$DB_NAME" "$BACKUP_PBKDF2_ITER" "$server" "$BACKUP_PBKDF2_ITER" > "$out.meta.json"
write_sha "$out"
echo "wrote $out"
