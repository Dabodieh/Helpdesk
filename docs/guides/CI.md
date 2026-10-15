# Continuous integration and security validation

Workflow: `.github/workflows/ci.yml`. It runs on pull requests, on pushes to `main`, `release/**` and `claude/**`, and by hand (`workflow_dispatch`). A protected `main`
needs no change to the workflow: make these jobs the required status checks of the branch protection rule. The workflow has `permissions: contents: read`, uses no
secrets, never deploys and never pushes an image. Third-party actions are pinned to commit SHAs and scanner images to digests; Dependabot proposes updates as pull requests.

## Jobs

| Job | What it proves | Fails when |
|---|---|---|
| `backend` | Release build (warnings are errors, so a NuGet advisory fails here); architecture tests (module boundaries, endpoint authorization metadata, department scope); mail spike tests; **department and requester isolation tests** (309) as their own step; the remaining integration and unit tests (679) | any compile warning, vulnerable package, failing test |
| `migrations` | every module migration applies to an empty disposable PostgreSQL 18.6; history rows equal the migration files; a second run is a no-op; `dotnet ef has-pending-model-changes` is clean for all seven contexts; then the backup tooling round trip (encrypted backup, verification, isolated restore, roles re-applied, least privilege, comparison, wrong-passphrase and damaged-file rejection) | schema drift, missing migration, backup tooling regression |
| `web` | `tsc -b`, lint (errors), Vitest, production build | any of them |
| `scripts` | every PowerShell script parses; Pester 3.4.0 suite (`ModeGuard`) | syntax error, failing test |
| `ops-scripts` | `bash -n` and ShellCheck (warnings fail) on the backup, restore and CI scripts | any finding |
| `compose` | `scripts/ci/validate-compose.py`: all profiles resolve with placeholder values only; no `:latest`; cloudflared pinned by digest; production publishes no ports; restart policies; production settings (dev sign-in off, no migrate-on-start, attachments off, certificate required); app uses `postgres-prod` and the DML-only role; secrets mounted read-only; development ports on loopback | any policy violation |
| `dependencies` | `dotnet list package --vulnerable --include-transitive`; `npm audit --omit=dev --audit-level=high`; deprecated packages and a full npm audit as informational reports | any vulnerable NuGet package, high/critical production npm advisory |
| `secrets` | gitleaks over the full history with `.gitleaks.toml` (default rules plus two reviewed, file-scoped false-positive entries) | any finding |
| `image` | builds `docker/host.Dockerfile` with the commit as revision label; non-root user; Trivy config scan; CycloneDX and SPDX SBOMs (artifact, 90 days); Trivy image scan for vulnerabilities and embedded secrets at HIGH/CRITICAL (unfixed findings are not hidden) | label mismatch, root user, any HIGH/CRITICAL finding or secret |

## Run the same checks locally

```bash
dotnet build Helpdesk.slnx -c Release
dotnet test tests/Helpdesk.ArchitectureTests -c Release --no-build
HELPDESK_TEST_DB="Host=localhost;Port=5432;Username=helpdesk;Password=...;Maximum Pool Size=20" \
  dotnet test tests/Helpdesk.Host.Tests -c Release --no-build --filter "FullyQualifiedName~Isolation"
npm --prefix src/web ci && npx --prefix src/web tsc -b && npm --prefix src/web run lint && npm --prefix src/web test
python3 scripts/ci/validate-compose.py
bash scripts/ci/check-migrations.sh        # needs DB_CONNECTION and PSQL_CMD for a DISPOSABLE database
docker run --rm -v "$PWD:/repo:ro" zricethezav/gitleaks:v8.30.1 detect --source /repo --config /repo/.gitleaks.toml --redact
```

## Not covered by CI (by design) and needing a GitHub-hosted run

- **The first real run.** Everything above was executed locally (commands, filters, scanners, workflow lint with actionlint), but the workflow itself has never run on
  GitHub. Expect the first run to need small fixes (runner image differences, action input names, Windows PowerShell module availability for Pester).
- Browser end-to-end tests (Playwright) need a running stack and are not in CI; run `scripts/e2e/e2e.ps1` locally before releases.
- Live Microsoft Graph, Entra and Cloudflare behaviour, restore of a real Production backup, and restart/recovery behaviour are manual (see `PRODUCTION_RECOVERY.md`).
- Image vulnerability findings are a point in time; the SBOM lets them be re-evaluated later. Re-run the `image` job (or Dependabot) regularly, not only on change.
- Branch protection, required reviews and who may push are GitHub settings, not repository files.
