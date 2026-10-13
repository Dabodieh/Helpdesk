# Targeted onboarding hardening — 2026-10-12

## Scope and sequence
Fix the four findings from the project review: atomic invitation acceptance, cancellation recovery during Entra setup, invitation URL confidentiality, and stale delivery documentation. Single-agent implementation; no unrelated feature work or production deployment.

## Reusable patterns
Reuse OrganisationDbContext transactions, MembershipService's Department lock and membership/audit rules, existing access-cache invalidation, Entra's per-session creation guard, React Router and existing CSRF-protected invitation POST APIs.

## Design and impact
- Invitation acceptance, membership creation and audits commit together. Existing memberships keep their role. Recheck the token hash when claiming to reject a link replaced concurrently.
- Entra cancellation cleans up only the newly created application with a bounded independent token and restores a retryable session. Do not delete an application after settings have committed.
- New invitation links use `/activate#<token>`. Keep the token in tab-scoped session storage through sign-in; never include it in the login return URL. Clear it on success or invalidation. Support old path links while redacting them in application request logs; upstream logs require operator redaction.
- No schema changes, dependencies, role changes or wider access. Existing JSON API shapes remain unchanged. Revise ADR-036/037 in place.

## Risks and verification
Test rollback on membership failure, concurrent activation and existing-role preservation, cancellation cleanup/retry, fragment activation through sign-in and legacy links, request-log redaction. Run only affected backend classes and frontend files plus the frontend build. Use isolated fixture databases; do not alter the existing PostgreSQL service or user credentials. Record any environment limitations honestly.

## Research
Microsoft EF Core transaction documentation confirms that acceptance and membership must share the transaction. MDN URI fragment documentation confirms fragments are not sent to the server; encoding the fragment into `returnUrl` would defeat this, so retain it locally through sign-in. OWASP logging guidance supports excluding tokens from logs.
- https://learn.microsoft.com/en-us/ef/core/saving/transactions
- https://developer.mozilla.org/en-US/docs/Web/URI/Reference/Fragment
- https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html

## Rollback
No database migration. Reverting URL handling alone would break newly issued links; retain fragment support or resend invitations when reverting. Never automatically delete a configured Microsoft application. Live tenant, restore-drill, retention and malware-scanner gates remain open.

## Progress
- Inspection, research, implementation, targeted verification and final diff review complete.
- Evidence and residual limitations: `../reports/TARGETED-HARDENING-2026-10-12.md`.
