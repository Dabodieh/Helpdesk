#!/usr/bin/env bash
# Copies the attachment bucket to an off-host target (the object store is part of the same recoverable system as the database; pair the two by timestamp).
#
#   S3_ENDPOINT=https://s3.example S3_BUCKET=helpdesk S3_ACCESS_KEY=... S3_SECRET_KEY=... \
#   OBJECT_BACKUP_DEST=s3://offhost-bucket/helpdesk-objects scripts/ops/backup-objects.sh
#
# Requires the AWS CLI on the host that runs it (any S3-compatible endpoint; the CLI is only a transfer tool, the application never depends on it).
# OBJECT_BACKUP_DEST is an s3:// URI or a local directory on the off-host mount. `sync` never deletes at the destination (no --delete), so a mistaken or
# emptied source cannot erase the copy. Credentials are read from the environment and never printed.
# Attachments are switched off in production until a malware scanner exists, so the bucket may legitimately be empty; the copy still runs and reports 0 objects.
set -euo pipefail

: "${S3_ENDPOINT:?S3 endpoint URL}"
: "${S3_BUCKET:?bucket name}"
: "${S3_ACCESS_KEY:?access key}"
: "${S3_SECRET_KEY:?secret key}"
: "${OBJECT_BACKUP_DEST:?s3:// URI or local directory to copy to}"
command -v aws >/dev/null || { echo "the AWS CLI ('aws') is required for object-store backup" >&2; exit 1; }

export AWS_ACCESS_KEY_ID="$S3_ACCESS_KEY" AWS_SECRET_ACCESS_KEY="$S3_SECRET_KEY" AWS_EC2_METADATA_DISABLED=true AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-us-east-1}"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
case "$OBJECT_BACKUP_DEST" in s3://*) ;; *) mkdir -p "$OBJECT_BACKUP_DEST";; esac

aws s3 sync "s3://$S3_BUCKET" "$OBJECT_BACKUP_DEST" --endpoint-url "$S3_ENDPOINT" --only-show-errors
# Verification: a second dry-run sync must have nothing left to copy (every source object is present at the destination with the same size and time).
pending="$(aws s3 sync "s3://$S3_BUCKET" "$OBJECT_BACKUP_DEST" --endpoint-url "$S3_ENDPOINT" --dryrun | grep -c '(dryrun)' || true)"
[ "$pending" = "0" ] || { echo "object backup incomplete: $pending object(s) not at the destination" >&2; exit 1; }
count="$(aws s3 ls "s3://$S3_BUCKET" --recursive --endpoint-url "$S3_ENDPOINT" | grep -c . || true)"
echo "objects copied and verified at $stamp: $count"
