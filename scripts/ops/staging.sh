#!/usr/bin/env bash
# The ONE entry point for the staging deployment. It runs the production compose file (docker/production/compose.yml) under the fixed project name
# "helpdesk-staging" with docker/staging/.env, so staging gets its own volumes (helpdesk-staging_pgdata), network and secrets and can never attach the
# development project's volumes (project "helpdesk"). It refuses anything that would delete volumes.
#
#   scripts/ops/staging.sh check              validate docker/staging/.env and the TLS files (no Docker needed)
#   scripts/ops/staging.sh config -q          validate the compose file with this environment
#   scripts/ops/staging.sh build              build the application image
#   scripts/ops/staging.sh migrate            apply database migrations (one-off container, then exits)
#   scripts/ops/staging.sh up -d              start / update the stack
#   scripts/ops/staging.sh verify             post-deployment checks (scripts/ops/staging-verify.sh)
#   scripts/ops/staging.sh ps | logs helpdesk | stop | restart helpdesk | down      (any other docker compose command works too)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_FILE="${STAGING_ENV_FILE:-$root/docker/staging/.env}"
PROJECT="helpdesk-staging"
COMPOSE_FILE="$root/docker/production/compose.yml"
fail() { echo "staging: $*" >&2; exit 1; }

[ -f "$ENV_FILE" ] || fail "missing $ENV_FILE (copy docker/staging/.env.example and fill it in)"
value() { sed -n "s/^$1=//p" "$ENV_FILE" | tail -1 | tr -d '\r'; }
[ "$(value COMPOSE_PROJECT_NAME)" = "$PROJECT" ] || fail "COMPOSE_PROJECT_NAME in $ENV_FILE must be $PROJECT (it isolates staging's volumes and network)"

check() {
  local bad=0 key
  for key in PUBLIC_HOST POSTGRES_PASSWORD ENTRA_TENANT_ID ENTRA_CLIENT_ID ENTRA_CLIENT_SECRET BOOTSTRAP_PLATFORM_ADMIN_SUBJECT \
             GRAPH_TENANT_ID GRAPH_CLIENT_ID GRAPH_CLIENT_SECRET S3_ENDPOINT S3_BUCKET S3_ACCESS_KEY S3_SECRET_KEY INTERNAL_SUBNET PROXY_IP TLS_DIR; do
    [ -n "$(value "$key")" ] || { echo "MISSING  $key" >&2; bad=1; }
  done
  case "$(value PUBLIC_HOST)" in *example.org*|*example.com*) echo "PLACEHOLDER  PUBLIC_HOST still holds the example value" >&2; bad=1;; esac
  [ "$(value S3_BUCKET)" != "helpdesk" ] || { echo "UNSAFE  S3_BUCKET must be a staging-only bucket, not the default 'helpdesk'" >&2; bad=1; }
  local tls; tls="$(value TLS_DIR)"; case "$tls" in /*|[A-Za-z]:*) ;; *) tls="$root/docker/production/$tls";; esac
  for f in fullchain.pem privkey.pem; do [ -s "$tls/$f" ] || { echo "MISSING  TLS file $tls/$f" >&2; bad=1; }; done
  [ "$bad" = 0 ] || fail "configuration incomplete (values are never printed)"
  echo "staging configuration complete (project $PROJECT, host $(value PUBLIC_HOST))"
}

case "${1:-}" in
  check) check; exit 0;;
  verify) shift; exec "$root/scripts/ops/staging-verify.sh" "$@";;
  migrate) set -- run --rm migrate;;
esac

for arg in "$@"; do
  case "$arg" in -v|--volumes|--rmi|prune) fail "refusing '$arg': staging volumes (database, Data Protection keys) are never deleted by this script";; esac
done

export COMPOSE_PROJECT_NAME="$PROJECT"
exec docker compose -p "$PROJECT" -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"
