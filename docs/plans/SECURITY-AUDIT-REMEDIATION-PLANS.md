# Security audit remediation — 2026-10-12

## Scope
Work through the existing F-01–F-13 audit in bounded slices. First slice: F-10's missing response headers. F-07 application fixes already have separate evidence. Keep policy and live-environment gates open until their evidence exists.

## Implementation and impact
Extend `ProductionHosting.UseResponseSecurityHeaders`, the existing application-owned middleware. Add Permissions-Policy denying camera, microphone and geolocation; COOP `same-origin`; CORP `same-origin`. No schema, API payload, UI, permission or audit-event changes. Preserve full-page OIDC redirects, CSP, caching and attachment authorization. Update ADR-020 and the deployment guide.

## Research and risk
MDN header references confirm the empty Permissions-Policy allowlist denies the named features, CORP restricts cross-origin no-cors resource loading, and COOP isolates opener relationships. The current app uses full-page authentication, not popup authentication. Future popup integrations or cross-origin resource consumers need deliberate review. No COEP is introduced. Real proxy/browser verification remains a deployment check.

- https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Permissions-Policy
- https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Cross-Origin-Opener-Policy
- https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Cross-Origin-Resource-Policy

## Verification and rollback
Extend the focused hosting response test to cover SPA HTML, static assets, API errors and authentication redirects; run that test against a disposable PostgreSQL 18 fixture. Revert the three header assignments to roll back; no data conversion or migration. Record results in the remediation tracker. No deployment or production-readiness claim.

## Next slices
F-06 administration/webhook abuse controls, then F-05 existing-tool dependency checks. Destructive retention, external key protection and session policy require the recorded organisational decisions; live restore/Graph evidence cannot be replaced with unit tests.

## Progress — 2026-10-13
Header slice implemented; focused hosting test passed (1/1). ADR and deployment guidance updated. See `../reports/SECURITY-REMEDIATION-STATUS-2026-10-13.md` for all findings and the development-environment recovery blocker discovered on resumption.
