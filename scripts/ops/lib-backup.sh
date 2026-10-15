#!/usr/bin/env bash
# Shared helpers for the backup scripts in this folder. Sourced, never run.
#
# Two ways to reach PostgreSQL, same scripts everywhere (Linux, Unraid, Docker Desktop through Git Bash):
#   - PG_CONTAINER=<name>   a running container (plain `docker exec`; e.g. helpdesk-postgres-prod-1). Preferred for the local Windows stack and Unraid.
#   - otherwise             `docker compose -f $COMPOSE_FILE --env-file $ENV_FILE exec $PG_SERVICE` (the reference topology in docker/production/compose.yml).
#
# Nothing here prints the passphrase or any credential. Only POSIX shell, docker, openssl (3.x), coreutils (sha256sum, stat, date) and tar are required.

# Git Bash on Windows: docker arguments (container paths such as /tmp, volume specs) must not be rewritten, but the native openssl/tar must get Windows paths.
docker() { MSYS_NO_PATHCONV=1 command docker "$@"; }
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

# One KDF setting for every script, so any file can be decrypted by hand: openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -pass file:<file> -in <backup>
BACKUP_PBKDF2_ITER="${BACKUP_PBKDF2_ITER:-600000}"
BACKUP_NAME_PREFIX="${BACKUP_NAME_PREFIX:-helpdesk}"
PG_CONTAINER="${PG_CONTAINER:-}"

if [ -n "$PG_CONTAINER" ]; then
  DB_NAME="${DB_NAME:-helpdesk_prod}"
  DB_USER="${DB_USER:-$(docker exec "$PG_CONTAINER" printenv POSTGRES_USER 2>/dev/null || true)}"
else
  COMPOSE_FILE="${COMPOSE_FILE:-docker/production/compose.yml}"
  ENV_FILE="${ENV_FILE:-docker/production/.env}"
  PG_SERVICE="${PG_SERVICE:-postgres}"
  DB_NAME="${DB_NAME:-helpdesk}"
  DB_USER="${DB_USER:-helpdesk}"
fi

# Runs a command inside the PostgreSQL container, stdin passed through.
pg_exec() {
  if [ -n "$PG_CONTAINER" ]; then docker exec -i "$PG_CONTAINER" "$@"
  else docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T "$PG_SERVICE" "$@"; fi
}

# Encrypt stdin to stdout / decrypt stdin to stdout with the passphrase file (never an argument or an environment variable).
enc() { openssl enc -aes-256-cbc -pbkdf2 -iter "$BACKUP_PBKDF2_ITER" -salt -pass "file:$(native_path "$BACKUP_PASSPHRASE_FILE")"; }
dec() { openssl enc -d -aes-256-cbc -pbkdf2 -iter "$BACKUP_PBKDF2_ITER" -pass "file:$(native_path "$BACKUP_PASSPHRASE_FILE")"; }

file_size() { stat -c %s "$1" 2>/dev/null || stat -f %z "$1"; }
utc_now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
stamp_now() { date -u +%Y%m%dT%H%M%SZ; }

# Checks the sidecar checksum of a file (no output on success).
sha_ok() { ( cd "$(dirname "$1")" && sha256sum -c --status "$(basename "$1").sha256" ); }
write_sha() { ( cd "$(dirname "$1")" && sha256sum "$(basename "$1")" > "$(basename "$1").sha256" ); }

require_passphrase() {
  : "${BACKUP_PASSPHRASE_FILE:?path of a file holding the encryption passphrase}"
  [ -s "$BACKUP_PASSPHRASE_FILE" ] || { echo "passphrase file is missing or empty" >&2; exit 1; }
}
