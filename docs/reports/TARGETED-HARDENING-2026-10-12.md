# Targeted hardening — 2026-10-12

## Outcome
The four findings in the project-state review are addressed: atomic invitation acceptance, Entra cancellation recovery, invitation URL confidentiality, and stale delivery documentation. No schema migration, new dependency, production deployment or change to Department access policy.

## Changes
- `InvitationService` claims the current token and adds membership in one transaction with both audit records. A failed membership insert rolls everything back and the original link can retry. Concurrent activation succeeds once. The claim rechecks the token hash after lookup.
- `MembershipService` reuses its existing Department lock, validation and member-add audit primitive. Invitation acceptance preserves existing roles under that lock. Cache invalidation and bounded best-effort access notifications happen after commit.
- `EntraSetupService` handles cancellation as well as other setup failures. Cleanup uses an independent 10-second token; cleanup cancellation cannot leave the session stuck. Check request cancellation before the settings commit; finish committing independently of a disconnected browser. Runtime reload is outside the cleanup block so its failure does not delete the saved application.
- Invitation links use `/activate#<token>`. `InvitationEntry` saves the token in tab-scoped session storage, replaces the browser address with `/activate`, then mounts authentication. No token is sent in the sign-in return URL. Activation reads the saved token and clears it after success or an invalid preview. Old path links still work. Application request-completion logs redact activation paths, and use the host's configured logger.
- Updated ADR-036/037, architecture, development guide, development plan and the historical remediation tracker's introductory note. Historical analytics and persisted Data Protection keys are now correctly described as implemented. The production guide documents remaining legacy-link logging precautions.

## Verification
Targeted checks only; no full suite or isolation matrix.

- Backend: **37 distinct tests passed across targeted passes** in `InvitationTests`, `EntraSetupTests`, and `MembershipApiTests`. The initial pass had 34 passes and three new-test failures: two used the wrong audit-column name, and the log test exposed an ineffective initial redaction callback. Corrected the tests/redaction, reran those three successfully, then the smallest affected group (`InvitationTests`, 11/11).
- Added real PostgreSQL fault injection for membership insertion, rollback/audit checks followed by retry, simultaneous activation, captured request-log redaction, and stubbed Microsoft cancellation/cleanup-timeout recovery followed by successful retry.
- Database: disposable PostgreSQL **18**, localhost port 55432, isolated from the existing Windows PostgreSQL service. Fixture databases are created/migrated/dropped by the existing harness. No existing database or credentials changed.
- Frontend: `people.test.tsx` **6/6**, `App.test.tsx` **4/4**. Covers fragment activation through the sign-in round trip, token-free return URL, storage cleanup after acceptance, legacy path support, and existing admin/auth behaviour. People tests rerun after the final component simplification.
- Changed-file Oxlint: pass. TypeScript/Vite production build: pass; existing large-bundle warning remains (about 871 kB minified JS).
- Final diff reviewed; whitespace check passed. No browser or live-tenant claim is made.

## Research and decisions
Microsoft EF Core transaction documentation informed the shared transaction; MDN fragment documentation informed keeping the token out of `returnUrl`; OWASP logging guidance informed secret exclusion. The installed Serilog 10 XML API documentation and upstream `RequestLoggingOptions` source confirmed that the built-in request-path property must be changed through `GetMessageTemplateProperties`, not diagnostic enrichment.

- https://learn.microsoft.com/en-us/ef/core/saving/transactions
- https://developer.mozilla.org/en-US/docs/Web/URI/Reference/Fragment
- https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html
- https://github.com/serilog/serilog-aspnetcore/blob/dev/src/Serilog.AspNetCore/AspNetCore/RequestLoggingOptions.cs

## Remaining limits and next step
Live Graph/Entra evidence, production backup/restore and deployment evidence, retention decisions and malware scanning remain open. Attachments stay disabled in production. A Microsoft application can still require manual removal if Graph cleanup fails or the process dies; stub tests are not tenant evidence. Legacy emailed path tokens necessarily reach upstream proxies on their first request: operators must redact those logs or resend invitations using the new format. Existing logs are not scrubbed.

Next: controlled staging and live Microsoft validation, followed by the recorded restore/deployment checks. No approval of production readiness is implied. No commit was requested or created.
