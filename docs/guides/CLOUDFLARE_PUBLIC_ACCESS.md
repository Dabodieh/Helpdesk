# Cloudflare public access: configuration record and activation gate

Status 2026-10-14: configured but NOT public. The tunnel is stopped, its ingress is the catch-all `http_status:404`, and Cloudflare Access allows exactly one email address.
Nothing here authorises starting the tunnel. `dev.ps1 tunnel-start -ConfirmPublic` is the only start path and it refuses unless the Production app is running.

## 1. Cloudflare state (applied through the Cloudflare API, verified by read-back)

| Item | Value |
|---|---|
| Zone | `amb-it.co.uk` (unrelated records untouched, including the `*` wildcard and the other tunnel CNAMEs) |
| Tunnel | `Helpdesk`, `<tunnel-id>`, remotely managed, no connector running |
| Tunnel ingress | one rule: `http_status:404` (the `<dev-pc-lan-address>:5173` route was removed on 2026-10-14) |
| DNS | `helpdesk` CNAME to `<tunnel>.cfargotunnel.com`, proxied (kept) |
| Access application | `SAMH Helpdesk`, id `<access-app-id>`, self-hosted, `helpdesk.amb-it.co.uk` (whole hostname including `/api` and `/hubs`) |
| Policy | `<access-allow-policy-id>` "Helpdesk: allow <operator>", ALLOW, include exactly `<operator-account>`, 8 h. Everyone else is denied by Access default-deny; no Deny, Bypass, Service Auth, domain or group rule exists. The earlier deny placeholder was removed |
| Identity provider | One-time PIN only (`<access-idp-id>`), auto-redirect to it |
| Session | 8 h, HttpOnly cookie, SameSite Lax, binding cookie on, hidden from the app launcher |
| Zero Trust org | `<team>.cloudflareaccess.com` |

Verified from outside: `/` and `/api/me` both return 302 to `<team>.cloudflareaccess.com`, so Access answers at the edge before any origin.

To add people: add their exact email addresses to this Allow policy (never "Everyone", a whole domain or "any valid email"); do not add an explicit Deny-everyone (it would override the Allow).

## 2. Production mode in the one `helpdesk` project (implemented, not started)

`docker-compose.yml` profiles: `development` (`host`, `web`), `production` (`app`, `objectstore`), `production-migrate` (`migrate`), `tunnel` (`cloudflared`), `storage`. `postgres` is shared and profile-less, so a plain `docker compose up -d` starts only the database.
`host` and `app` share `container_name: helpdesk-backend`; Compose refuses to render a project in which both are enabled, so Development and Production cannot run together.

| | Development | Production (`app`) |
|---|---|---|
| Environment | Development, dev sign-in | `ASPNETCORE_ENVIRONMENT=Production`; `Authentication__DevSignIn__Enabled=false` (the host also refuses `true` outside Development/Testing) |
| Frontend | Vite (`web`) | compiled SPA served by the host; no Vite |
| Published ports | 127.0.0.1:5173/5080/5433 | none (the object store stays on 127.0.0.1:8333) |
| Database | `helpdesk` | `PROD_DB_NAME` (default `helpdesk_prod`) on the same server and volume; created and migrated only by `dev.ps1 prod-migrate` |
| Migrations | on start | never on start (`Database__MigrateOnStartup=false`) |
| Sign-in / cookies | dev | Entra OIDC; Secure cookies outside Development |
| Proxy trust | none | `Proxy__KnownNetworks__0=${PROXY_KNOWN_NETWORKS:-172.16.0.0/12}`; safe because nothing is published and only `cloudflared` shares the network. Client address, limits and the outstanding live check: `CLIENT_ADDRESS_AND_RATE_LIMITS.md` (S-03) |
| Hosts / origins | any | `AllowedHosts` and `Realtime__AllowedOrigins__0` = `PUBLIC_HOST` (https) |
| Key ring | plain | certificate required (`DataProtection__RequireCertificate=true`) |
| Mail / storage | Fake / in-memory | Graph / S3 (`objectstore`); attachments disabled until a scanner exists (D-08) |

Settings come from the git-ignored root `.env`; unset secrets are empty and the host fails closed. `dev.ps1 prod-check` lists missing names without values. Existing rate limits, security headers and fail-closed start-up checks are in the same image and apply unchanged.

## 3. Origin routing and tunnel safety

- Future route (only after activation approval): `helpdesk.amb-it.co.uk` to `http://app:8080`. Never `web`, `host`, `localhost` or an IP.
- `cloudflared` depends on `app` being healthy and carries the `tunnel` profile; no other command starts it; `restart: "no"`.
- `tunnel-start` refuses unless the backend container is running with `ASPNETCORE_ENVIRONMENT=Production` and `-ConfirmPublic` is passed.
- The token is only in the git-ignored `.tunnel.env`.

## 4. Remaining before activation

1. Further allowed emails, if any (section 1).
2. Entra sign-in registration in the personal test tenant and the Graph registration and mailboxes: `M365_TEST_TENANT_CHECKLIST.md` G1 to G4, I4 and J (approvals pending). Redirect URI: `https://helpdesk.amb-it.co.uk/api/auth/signin-oidc`.
3. Production values in `.env` and a secrets folder outside the repository (disposable certificates exist in `%USERPROFILE%\HelpdeskSecrets\m365`; reusing them for a public instance needs your decision).
4. `dev.ps1 prod-check` clean, then `prod-migrate` and `prod-start`; verify on the Docker network only (dev-login 404, `/api/auth/providers` returns `{"entra":true}`, object-store bucket exists).
5. Backup of the new database, your explicit approval, then add the tunnel route to `http://app:8080` and run `dev.ps1 tunnel-start -ConfirmPublic`.

## 5. Certificates (production; not yet generated)

`scripts\windows\new-production-certs.ps1 -Approved` creates a Data Protection certificate and a Graph client certificate in `%USERPROFILE%\HelpdeskSecrets\production` (this Windows user only), never overwriting and never touching the disposable `m365` folder. Back up the whole folder BEFORE first use: an offline encrypted copy (for example a BitLocker USB kept apart from the PC) plus the two password files in your password manager. The host refuses to start without the Data Protection certificate, and a key ring encrypted with a lost certificate cannot be read, so keep two copies. Rotation and recovery: `PRODUCTION_DEPLOYMENT.md` (Data Protection section). The Graph certificate expires in 1 year; upload the new public `.cer` before the old one lapses.

## 6. Production object store and database (prepared, not created)

`objectstore-prod` is a separate SeaweedFS with its own volume `objectdata_prod`, no published port, reachable only from `app`; it shares nothing with the development `objectstore`/`objectdata`, E2E or `helpdesk-pv`. Attachments stay disabled (`Attachments__Enabled=false`; the host refuses `true` until a malware scanner exists). `helpdesk_prod` does not exist yet: `dev.ps1 prod-migrate` creates it on the existing server (never touching `helpdesk`) and migrates it, and refuses while required settings are missing.

## 7. Status after local preparation (2026-10-14)

- Certificates generated and validated (private key present, signs and verifies; ACL: this user only): `dp.pfx` (thumbprint `A03A390F...78F8`, 5 years) and `graph.pfx` (`7858DEA0...52BF`, 1 year). Not yet backed up offline: required before first use (section 5).
- `.env` (git-ignored) now holds `PROD_DB_NAME`, `PUBLIC_HOST`, generated `PROD_S3_*` keys, `SECRETS_DIR`, `DP_CERT_*`, `GRAPH_CERT_*`. Still empty: `ENTRA_*`, `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT`, `GRAPH_TENANT_ID`, `GRAPH_CLIENT_ID` (they come from the tenant registrations).
- `helpdesk_prod` created and migrated with `dev.ps1 prod-migrate` (59 tables, 0 rows); development database unchanged (398 users, 14 tickets, 6 departments).
- `objectdata_prod` is created by Compose on the first `prod-start`; the bucket is created by the app on first use. Attachments remain disabled.

- 2026-10-14 (later): `.env` now holds the real `ENTRA_*`, `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT`, `GRAPH_*` values (verified by name and format; certificates open with their password files). Container health check now sends `Host: ${PUBLIC_HOST}` (AllowedHosts host filtering would answer `localhost` with 400). Step-by-step validation: `PRODUCTION_INTEGRATION_VALIDATION.md`.
