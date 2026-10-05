# ADR-003: Authentication via Entra ID OIDC with server-side sessions

Status: Accepted (2026-10-05)

## Context
Organisation uses Microsoft 365; more providers may follow.

## Decision
OIDC code flow + PKCE against a single Entra tenant; the API issues an HttpOnly SameSite cookie session; no tokens in browser storage; users keyed by (tenant, object id); provisioning on first login grants no access. Provider hidden behind an IAuthProvider seam.

## Alternatives considered
SPA-held bearer tokens (XSS exposure); ASP.NET Identity local accounts (not needed).

## Consequences
+ Smaller XSS blast radius, simple SPA. - CSRF protection required; cookie session needs same-site deployment of SPA and API.
