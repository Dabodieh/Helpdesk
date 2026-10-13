# ADR-037: Guided Microsoft Entra sign-in setup, stored in the database

Status: accepted (tested against a stub of Microsoft's endpoints only; not yet run against a real tenant)

## Context

Staff sign-in used Entra settings that could only be supplied as server settings, and the app refused to start without them (or dev sign-in). Administrators wanted to connect Entra from the product itself.

## Decision

- **Who and where:** a signed-in **platform administrator** on Administration > Microsoft 365 (`platform.users.manage`). There is no open first-run page: a brand-new install with no sign-in still takes server settings once (or development sign-in).
- **Bootstrap sign-in:** the administrator signs in to Microsoft with a **device code** using Microsoft's own public client "Microsoft Graph Command Line Tools" (`EntraSetup:BootstrapClientId`), consenting for their own session to `Application.ReadWrite.All`. Nothing is granted tenant-wide. The access token lives only in server memory for the session; it is never stored, logged or returned. The account needs Application Administrator or Global Administrator.
- **What is created, once per sign-in:** one single-tenant application registration "Helpdesk staff sign-in" (`AzureADMyOrg`) with redirect URIs for the origins the administrator confirms (https, or http for localhost), no API permissions, its service principal, and one client secret (12 months, `EntraSetup:SecretMonths`). A failure or cancellation before saving settings attempts to delete only the application just made, using an independent 10-second cleanup token. Even a cleanup timeout releases the session for retry; if Microsoft cannot be reached, an orphan may need manual removal. Request cancellation is checked before committing settings; the commit then finishes independently of the browser. Reloading runtime options happens after saving, outside the cleanup block, so a reload failure cannot delete the configured application. Nothing else in the tenant is read or changed. Mail identity and Exchange scoping are unchanged (ADR-010/011, ADR-035).
- **Storage:** `identity.entra_settings` (single row). The secret is protected with ASP.NET Data Protection; the key ring lives in the same database and is not encrypted at rest (production gate), so the database and its backups are the boundary. If the keys are lost the secret cannot be read and sign-in stays off until reconnected. Never returned by the API, never audited.
- **Applied without restart:** the OpenID Connect scheme is always registered and configured from `EntraSettingsStore`; saving or removing the row reloads it and clears the handler's options. Server settings/environment, when present, **always win** and the wizard refuses. Single instance; a second running copy would load the change on restart.
- **Redirect address:** taken from the registered origins by the host the request was made to (never from a header), so it is correct behind a proxy or tunnel without trusting forwarded headers.
- **First platform admin:** the Entra object id of the administrator who ran the setup is stored; their first Entra sign-in becomes a platform administrator (audited), like `BootstrapPlatformAdminSubjects`.
- **Startup:** having no sign-in configured is no longer a startup failure (otherwise this feature could not exist). `/api/auth/login` answers 404, `/api/auth/providers` says Entra is not connected, and the sign-in page says setup is needed. Partial or invalid server settings still fail startup.

## Consequences

- Rotating the secret means running the wizard again (a new application and secret; the old application is left in the tenant and its id reported). The page warns when the secret expires within 30 days.
- Development sign-in remains an open door while enabled; turn it off in server settings once Microsoft sign-in works.
- Real-tenant behaviour (device-code consent screens, replication delay of new applications, conditional-access policies blocking the bootstrap client) is unvalidated (D-16).
