#!/usr/bin/env bash
# Encrypted bundle of the configuration a restore needs and the database does NOT hold: the environment file, the key-ring and Graph certificates with their password
# files, tunnel token file. Same passphrase and cipher as the database backup, so one passphrase (kept in a password manager) recovers everything.
#
#   BACKUP_PASSPHRASE_FILE=... BACKUP_DIR=... \
#   CONFIG_PATHS="/opt/helpdesk/.env;/opt/helpdesk/.tunnel.env;/opt/helpdesk-secrets" scripts/ops/backup-config.sh
#
# CONFIG_PATHS is a semicolon-separated list of files or folders (missing entries are reported and skipped, but at least one must exist). Output:
# <BACKUP_DIR>/<prefix>-config-<UTC>.tar.enc + .sha256. The bundle contains SECRETS: its protection is the passphrase, so never store the passphrase beside it.
# Contents are listed (names only) on success; values are never printed. The bundle is a copy: it does not move or delete the originals.
set -euo pipefail
umask 077
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$here/lib-backup.sh"

require_passphrase
: "${BACKUP_DIR:?directory to write the bundle to}"
: "${CONFIG_PATHS:?semicolon-separated files and folders to include}"
mkdir -p "$BACKUP_DIR"
out="$BACKUP_DIR/$BACKUP_NAME_PREFIX-config-$(stamp_now).tar.enc"
cleanup() { local rc=$?; [ "$rc" -eq 0 ] || rm -f "$out" "$out.sha256"; rm -rf "${stage:-}"; }
trap cleanup EXIT

# Stage under one root with neutral names so the archive does not depend on the host's path layout.
stage="$(mktemp -d)"
n=0
IFS=';' read -r -a items <<< "$CONFIG_PATHS"
for item in "${items[@]}"; do
  [ -n "$item" ] || continue
  if [ -e "$item" ]; then
    n=$((n + 1))
    mkdir -p "$stage/$(printf '%02d' "$n")"
    cp -R "$item" "$stage/$(printf '%02d' "$n")/"
  else
    echo "skipped (not found): $(basename "$item")" >&2
  fi
done
[ "$n" -gt 0 ] || { echo "none of the configuration paths exists" >&2; exit 1; }
tar cf - -C "$stage" . | enc > "$out"
[ "$(file_size "$out")" -gt 64 ] || { echo "configuration bundle is implausibly small; removed" >&2; exit 1; }
members="$(dec < "$out" | tar tf - | grep -c -v '/$' || true)"
write_sha "$out"
echo "wrote $out (files: $members)"
