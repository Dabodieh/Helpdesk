#!/usr/bin/env bash
# Post-deployment verification of the staging stack. Read-only: it only reads health endpoints, headers, container state and the Hangfire job registry.
# Prints PASS/FAIL per check and exits non-zero if any check failed. Values from docker/staging/.env are used, never printed.
#
#   scripts/ops/staging.sh verify            (optional: STAGING_RESOLVE_IP=203.0.113.10 to test before DNS exists)
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_FILE="${STAGING_ENV_FILE:-$root/docker/staging/.env}"
value() { sed -n "s/^$1=//p" "$ENV_FILE" | tail -1 | tr -d '\r'; }
host="$(value PUBLIC_HOST)"; https_port="$(value HTTPS_PORT)"; https_port="${https_port:-443}"; http_port="$(value HTTP_PORT)"; http_port="${http_port:-80}"
resolve=(); [ -z "${STAGING_RESOLVE_IP:-}" ] || resolve=(--resolve "$host:$https_port:$STAGING_RESOLVE_IP" --resolve "$host:$http_port:$STAGING_RESOLVE_IP")
stack() { "$root/scripts/ops/staging.sh" "$@"; }
failures=0
check() { # name, expected, actual
  if [ "$2" = "$3" ]; then echo "PASS  $1"; else echo "FAIL  $1 (expected $2, got $3)"; failures=$((failures + 1)); fi
}
code() { curl -s -o /dev/null -m 15 -w '%{http_code}' "${resolve[@]}" "$@" || true; }
base="https://$host:$https_port"

running="$(stack ps --status running --services 2>/dev/null | sort | tr '\n' ' ')"
check "containers running (helpdesk nginx postgres)" "helpdesk nginx postgres " "$running"
check "http redirects to https" "301" "$(code "http://$host:$http_port/")"
check "readiness (database, migrations, object store)" "200" "$(code "$base/health/ready")"
check "liveness" "200" "$(code "$base/health/live")"
check "API requires sign-in" "401" "$(code "$base/api/tickets")"
check "dev sign-in route absent" "404" "$(code "$base/api/auth/dev-login?subject=x")"
check "Hangfire dashboard not anonymous" "no" "$([ "$(code "$base/hangfire")" = "200" ] && echo yes || echo no)"
check "HSTS header present" "yes" "$(curl -s -m 15 -I "${resolve[@]}" "$base/" | grep -qi '^strict-transport-security' && echo yes || echo no)"
check "nosniff header present" "yes" "$(curl -s -m 15 -I "${resolve[@]}" "$base/" | grep -qi '^x-content-type-options: *nosniff' && echo yes || echo no)"

expiry="$(echo | openssl s_client -connect "${STAGING_RESOLVE_IP:-$host}:$https_port" -servername "$host" 2>/dev/null | openssl x509 -noout -enddate 2>/dev/null | sed 's/notAfter=//')"
days=-1; [ -z "$expiry" ] || days=$(( ( $(date -u -d "$expiry" +%s) - $(date -u +%s) ) / 86400 ))
check "TLS certificate valid for more than 14 days" "yes" "$([ "$days" -gt 14 ] && echo yes || echo no)"

jobs="$(stack exec -T postgres psql -U helpdesk -d helpdesk -tAc "SELECT count(*) FROM hangfire.set WHERE key='recurring-jobs'" 2>/dev/null | tr -d '[:space:]')"
check "background workers registered (5 recurring jobs)" "5" "${jobs:-0}"
errors="$(stack logs --since 10m helpdesk 2>/dev/null | grep -c -E '\[(ERR|FTL)\]' || true)"
check "no application errors in the last 10 minutes" "0" "${errors:-0}"

if [ -n "${BACKUP_STATUS_FILE:-}" ]; then
  BACKUP_STATUS_FILE="$BACKUP_STATUS_FILE" "$root/scripts/ops/check-backup-freshness.sh" >/dev/null 2>&1 && echo "PASS  hourly backup fresh" || { echo "FAIL  hourly backup fresh"; failures=$((failures + 1)); }
else
  echo "SKIP  hourly backup fresh (set BACKUP_STATUS_FILE to check)"
fi

[ "$failures" = 0 ] && echo "staging verification passed" || { echo "staging verification FAILED: $failures check(s)" >&2; exit 1; }
