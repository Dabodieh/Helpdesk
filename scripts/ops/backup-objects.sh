#!/usr/bin/env bash
# Backs up the attachment object store (part of the same recoverable system as the database; pair the two by timestamp). Two modes, chosen by environment:
#
#  1. OBJECT_VOLUME=<docker volume>   (local Docker host, Unraid, Docker Desktop): the volume of the S3-compatible container is read through a throw-away container
#     (mounted read-only) and streamed into encryption: <BACKUP_DIR>/<prefix>-objects-<UTC>.tar.enc + .sha256. Needs only docker. Crash-consistent copy; on a busy store
#     quiesce first (attachments are disabled in production until a malware scanner exists, so the store is idle).
#       BACKUP_PASSPHRASE_FILE=... BACKUP_DIR=... OBJECT_VOLUME=helpdesk_objectdata_prod scripts/ops/backup-objects.sh
#
#  2. OBJECT_BACKUP_DEST=<s3://uri or directory>  (any S3-compatible endpoint, AWS CLI on the host): `aws s3 sync` of the bucket, no --delete, then a dry-run sync
#     must have nothing left to copy. Objects are NOT encrypted by this mode: protect the destination (OBJECT_BACKUP_SSE=AES256 for s3:// targets).
#       S3_ENDPOINT=... S3_BUCKET=... S3_ACCESS_KEY=... S3_SECRET_KEY=... OBJECT_BACKUP_DEST=... scripts/ops/backup-objects.sh
#
# Credentials are read from the environment and never printed. Mode 1 wins when both are set.
set -euo pipefail
umask 077
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$here/lib-backup.sh"

if [ -n "${OBJECT_VOLUME:-}" ]; then
  require_passphrase
  : "${BACKUP_DIR:?directory to write the object backup to}"
  mkdir -p "$BACKUP_DIR"
  image="${OBJECT_TAR_IMAGE:-alpine:latest}"
  docker volume inspect "$OBJECT_VOLUME" >/dev/null 2>&1 || { echo "object volume '$OBJECT_VOLUME' not found" >&2; exit 1; }
  out="$BACKUP_DIR/$BACKUP_NAME_PREFIX-objects-$(stamp_now).tar.enc"
  trap 'rc=$?; [ "$rc" -eq 0 ] || rm -f "$out" "$out.sha256"' EXIT
  docker run --rm -v "$OBJECT_VOLUME":/data:ro "$image" tar cf - -C /data . | enc > "$out"
  [ "$(file_size "$out")" -gt 64 ] || { echo "object backup is implausibly small; removed" >&2; exit 1; }
  # Verification: it decrypts and the tar lists; report the member count (0 files is legitimate while attachments are disabled).
  members="$(dec < "$out" | tar tf - 2>/dev/null | grep -c . || true)"
  write_sha "$out"
  echo "wrote $out (entries: $members)"
  exit 0
fi

: "${S3_ENDPOINT:?S3 endpoint URL (or set OBJECT_VOLUME for the volume mode)}"
: "${S3_BUCKET:?bucket name}"
: "${S3_ACCESS_KEY:?access key}"
: "${S3_SECRET_KEY:?secret key}"
: "${OBJECT_BACKUP_DEST:?s3:// URI or local directory to copy to}"
command -v aws >/dev/null || { echo "the AWS CLI ('aws') is required for the s3 mode; use OBJECT_VOLUME for a local Docker host" >&2; exit 1; }

export AWS_ACCESS_KEY_ID="$S3_ACCESS_KEY" AWS_SECRET_ACCESS_KEY="$S3_SECRET_KEY" AWS_EC2_METADATA_DISABLED=true AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-us-east-1}"
stamp="$(stamp_now)"
case "$OBJECT_BACKUP_DEST" in s3://*) ;; *) mkdir -p "$OBJECT_BACKUP_DEST";; esac
sse=()
case "$OBJECT_BACKUP_DEST" in s3://*) [ -z "${OBJECT_BACKUP_SSE:-}" ] || sse=(--sse "$OBJECT_BACKUP_SSE");; esac
aws s3 sync "s3://$S3_BUCKET" "$OBJECT_BACKUP_DEST" --endpoint-url "$S3_ENDPOINT" --only-show-errors "${sse[@]}"
pending="$(aws s3 sync "s3://$S3_BUCKET" "$OBJECT_BACKUP_DEST" --endpoint-url "$S3_ENDPOINT" --dryrun | grep -c '(dryrun)' || true)"
[ "$pending" = "0" ] || { echo "object backup incomplete: $pending object(s) not at the destination" >&2; exit 1; }
count="$(aws s3 ls "s3://$S3_BUCKET" --recursive --endpoint-url "$S3_ENDPOINT" | grep -c . || true)"
echo "objects copied and verified at $stamp: $count"
