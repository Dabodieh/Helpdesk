#!/usr/bin/env bash
# Encrypted logical backup of the Helpdesk PostgreSQL database (decision D3: RPO 1 hour, encrypted, kept OFF the Helpdesk host).
#
#   BACKUP_PASSPHRASE_FILE=/secure/backup.pass BACKUP_DIR=/mnt/offhost-staging scripts/ops/backup-database.sh
#
# Writes <BACKUP_DIR>/helpdesk-<UTC timestamp>.dump.enc and a .sha256 next to it. Copy both to the off-host target (separate from this host's infrastructure) and
# pair them with the object-storage state taken at the same time: the database and the attachment store are ONE recoverable system (docs/guides/BACKUP_AND_RESTORE.md).
# Run at least hourly. The passphrase file is never printed; keep a copy of it somewhere that survives the loss of this host.
set -euo pipefail
umask 077 # backups and their checksums are readable by the backup user only

: "${BACKUP_PASSPHRASE_FILE:?path of a file holding the encryption passphrase}"
: "${BACKUP_DIR:?directory to write the backup to}"
COMPOSE_FILE="${COMPOSE_FILE:-docker/production/compose.yml}"
DB_NAME="${DB_NAME:-helpdesk}"; DB_USER="${DB_USER:-helpdesk}"; PG_SERVICE="${PG_SERVICE:-postgres}"
ENV_FILE="${ENV_FILE:-docker/production/.env}"
[ -s "$BACKUP_PASSPHRASE_FILE" ] || { echo "passphrase file is missing or empty" >&2; exit 1; }
mkdir -p "$BACKUP_DIR"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
out="$BACKUP_DIR/helpdesk-$stamp.dump.enc"
# A failed or interrupted run must not leave a partial file that looks like a backup.
trap 'rc=$?; [ "$rc" -eq 0 ] || rm -f "$out" "$out.sha256"' EXIT

# Custom-format dump streamed straight into encryption: no plaintext copy of the data is ever written to disk.
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T "$PG_SERVICE" pg_dump -U "$DB_USER" -d "$DB_NAME" --format=custom --no-owner \
  | openssl enc -aes-256-cbc -pbkdf2 -salt -pass "file:$BACKUP_PASSPHRASE_FILE" -out "$out"

# An empty or truncated dump must never be mistaken for a backup.
[ "$(stat -c %s "$out" 2>/dev/null || stat -f %z "$out")" -gt 1024 ] || { rm -f "$out"; echo "backup is implausibly small; removed" >&2; exit 1; }
( cd "$BACKUP_DIR" && sha256sum "$(basename "$out")" > "$(basename "$out").sha256" )
echo "wrote $out"
