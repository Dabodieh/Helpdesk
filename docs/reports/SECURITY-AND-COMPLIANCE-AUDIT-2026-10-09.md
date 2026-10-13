# Security and compliance audit: 2026-10-09

Remediation tracker (2026-10-13): [current status](SECURITY-REMEDIATION-STATUS-2026-10-13.md). F-10's missing headers are now implemented and covered by a passing focused host test; deployment evidence remains open. Original findings below are retained as the audit baseline.

Follow-up (2026-10-12): F-07's new-link exposure is addressed by fragment URLs, tab-scoped storage through sign-in, and application request-path redaction. Legacy links still require upstream log redaction or resending. See `TARGETED-HARDENING-2026-10-12.md` for code and test evidence; the remaining production gates below are unchanged.

Purpose: establish, with evidence, whether Helpdesk could be run self-hosted on premises with staff data, what stands in the way, and the order in which to clear it.

**This is an engineering gap analysis, not a certification and not legal advice.** Nothing here makes the system "compliant" with UK GDPR, the Data Protection Act 2018 or ISO/IEC 27001. ISO standards are not laws: they are standards an organisation is assessed against, and ISO 27001 certifies an organisation's management system (policies, risk assessment, people, process), not a piece of software. The legal reading of UK GDPR belongs to your Data Protection Officer or legal adviser. What this report can do is show which technical controls exist, which are missing, and which decisions only your organisation can make.

## 1. Method and limits

Read-only. No code was changed for this audit and nothing was deployed.

| Check | How | Result |
|---|---|---|
| .NET dependencies, including transitive | `dotnet list package --vulnerable --include-transitive` on the whole solution | **0 vulnerable** in all 12 projects |
| .NET deprecated packages | `dotnet list package --deprecated` | xunit 2.9.3 marked "Legacy" in the 3 test projects (test-only, not shipped) |
| npm dependencies | `npm audit` (production and development) | **0 vulnerabilities** (0 critical/high/moderate/low) |
| npm outdated | `npm outdated` | 4 behind latest, 3 are major versions (typescript 7, jsdom 30, @types/node 26), Playwright is a minor. None are security fixes |
| Secrets in the repository | `git ls-files` for key/cert/env files; pattern search for hard-coded credentials | none found (compose files require `POSTGRES_PASSWORD` from `.env`; the only match is a placeholder string) |
| Response headers | read `ProductionHosting.cs`; checked live headers on the running host | present, see F-10 |
| Container build | read `host.Dockerfile`, compose files, nginx reference config | non-root user, TLS 1.2/1.3 only, see F-05 |
| Code review of controls | targeted reading of authentication, authorisation, rate limiting, logging, Hangfire dashboard, backup scripts, existing readiness report and security docs | findings below |

Not done, and needed before any assurance claim: an independent penetration test; a container image vulnerability scan (no scanner is installed, and I did not install one without approval); a run in a real deployment behind TLS; a real backup restore; live Microsoft Graph and Entra validation; a review by a data protection professional.

## 2. Findings, ranked

Severity is about risk to staff data and to an audit outcome, not effort.

| ID | Severity | Finding | Evidence | Fix | Owner |
|---|---|---|---|---|---|
| F-01 | High | **No personal data lifecycle.** There is no retention enforcement and no tooling for subject access, rectification, erasure or export. Raw email bodies are kept in `mail.messages`, ticket text is free text, audit and operational tables grow without limit | `docs/plans/OPEN_DECISIONS.md` D-09 (open); no erasure/anonymise/export code found; `SECURITY.md` open items | Decide retention periods and the lawful basis per data class, then build retention jobs, an export-for-subject-access report and an anonymise/erase path that respects the audit trail | You decide (DPO/legal), then me |
| F-02 | High | **Restore has never been proven on a real host**, no named owner, no hourly backup running | Readiness review 2026-10-08, risks "High" row; scripts exist and passed against test data only | Name an owner; schedule `backup-all.sh`; run and record one restore drill (`BACKUP_AND_RESTORE.md` checklist) | You |
| F-03 | High | **Development sign-in must never be reachable with real data.** It is on in the Development environment and grants platform admin to anyone who knows the URL pattern. Production start-up already refuses it, but the demo tunnel exposed a development instance to the internet | `AuthEndpoints.cs`; `appsettings.Development.json`; tunnel was active in this session | Remove the tunnel (you said you will); never run the development environment on a network that holds real data; keep the production guard | You |
| F-04 | Medium | **Secrets are protected with keys held in the same database.** The Data Protection key ring and the new stored Entra client secret share one database, and the key ring is not encrypted at rest. A copy of the database or a backup yields both. Audit data is also unencrypted at rest | `DataProtectionKeyStore.cs`; `EntraRuntime.cs`; readiness review | Full-disk encryption on the host (baseline). Then protect the key ring with a key held outside the database (a key store or an encrypted key file with restricted access) | You (host), then me |
| F-05 | Medium | **No automated supply-chain checks.** CI builds, lints and tests but runs no dependency audit, secret scan, image scan or SBOM. Base images are referenced by tag (`mcr.microsoft.com/dotnet/aspnet:10.0`, `node:24-alpine`), not by digest | `.github/workflows/ci.yml`; `docker/host.Dockerfile` | Add `dotnet list package --vulnerable`, `npm audit` and a secret scan to CI; scan images; pin digests; produce an SBOM. Today's manual scans were clean | Me (CI changes need your approval for any new tool) |
| F-06 | Medium | **Rate limiting is partial.** Limits cover portal create, portal reply and search only. Administration endpoints (including the new invitation and Entra setup endpoints) and the mail webhooks are not limited. Credential guessing is not a local risk because there are no local passwords (Entra handles it) | `AbuseControls.cs`; `SECURITY.md` ("no webhook rate limiting") | Add a limit policy for administration and webhook endpoints | Me |
| F-07 | Medium | **Invitation links carry the secret in the URL path** (`/activate/<token>`). It then appears in the application request log, the reverse proxy access log and browser history. Mitigations already in place: single use, expiry, only the invited Microsoft account can use it, `Referrer-Policy: no-referrer` | `InvitationService.cs`; `UseSerilogRequestLogging` | Put the token in the URL fragment (`#`), which browsers never send to a server, and redact the path in logs. Small change, my own feature | Me |
| F-08 | Medium | **Session policy is minimal.** Cookie lifetime 8 hours, sliding, no absolute maximum, no step-up for platform administration. A platform admin can add themselves to a department (audited, D-11) | `AuthenticationSetup.cs`; D-11 | Decide an absolute session limit and whether platform admin actions need recent sign-in; consider dual control for D-11 | You decide, then me |
| F-09 | Medium | **Logging and monitoring are not an operational system yet.** Logs go to the console/OpenTelemetry if configured; there is no stated log retention, no alert routing, no review routine | `Program.cs` Serilog setup; `OPERATIONS_RUNBOOK.md` | Choose where logs are kept and for how long, who is alerted and who reviews | You |
| F-10 | Low | **Header polish.** CSP allows inline styles (`style-src 'unsafe-inline'`); no `Permissions-Policy`, `Cross-Origin-Opener-Policy` or `Cross-Origin-Resource-Policy`. HSTS is added by the application in production only | `ProductionHosting.cs`; live response headers | Add the missing headers; verify HSTS behind the real proxy | Me |
| F-11 | Low | **Availability.** Single instance by design (jobs, presence and mail loop assume one process, ADR-021). No high availability or documented disaster recovery beyond restore | ADR-021; readiness review | State the accepted RTO/RPO to the business (already 1 h / 4 h targets) and document recovery | You |
| F-12 | Low | **Test-only packages** (xunit 2.9.3, "Legacy") and 4 outdated front-end packages | audit output above | Planned upgrades, not urgent | Me |
| F-13 | Open gates | Graph never validated on a real tenant (D-16); no malware scanner, so attachments are disabled in production (D-08, accepted and enforced at start-up); Entra setup and invitations tested only against stubs | `PRODUCTION-READINESS-2026-10-08.md`; ADR-035/036/037 | Test tenant; scanner decision; one controlled live run | You (tenant, scanner), then me |

### Controls that exist and help (evidence in the repository)

- Department is the hard security boundary, enforced server-side by one authoriser, with architecture tests and isolation matrix tests; inaccessible objects return 404; internal notes are excluded from requester paths.
- No local passwords: staff sign in with Microsoft Entra. Cookies are `HttpOnly`, `Secure` in production, `SameSite`; every state-changing request needs an antiforgery token; WebSocket origin is checked.
- Append-only audit trail for administrative changes; secrets, tokens and message bodies are not audited or logged by design.
- Production start-up refuses unsafe settings (development sign-in, mail simulator, attachments enabled without a scanner).
- Parameterised data access (EF Core); sanitised email HTML rendered only in a sandboxed frame; CSP blocks inline script and framing.
- Non-root container, TLS 1.2/1.3 only in the reference proxy config, forwarded headers trusted only from named proxies.
- Encrypted, verified, off-host-capable backup scripts and an operations runbook.
- The reporting rules forbid agent ranking and surveillance metrics, which supports UK GDPR data minimisation and fairness for staff data.

## 3. Map to what you will be asked

### UK GDPR / Data Protection Act 2018 (controller duties; the software supports them, it does not satisfy them alone)

| Requirement | Status | What is missing (owner) |
|---|---|---|
| Art 5 principles: lawfulness, purpose limitation, data minimisation, accuracy, storage limitation, security, accountability | Partly. Minimisation and security by design are visible. **Storage limitation is not met** (F-01) | Retention schedule and enforcement (you decide, me build) |
| Art 6 lawful basis, Art 9 special category | Not recorded | Document the basis per data class; tickets can contain special-category data in free text (health, etc.), so decide handling (DPO) |
| Art 12-14 transparency | Not provided | Privacy notice for staff and requesters (you) |
| Art 15-17, 20 subject access, rectification, erasure, portability | **Not built** (F-01) | Build after the policy is decided (me) |
| Art 24, 25 accountability, data protection by design and default | Good design record in ADRs and security docs | Keep ADRs current; record decisions (you) |
| Art 28 processors | Microsoft (Entra, Exchange Online, Graph) is a processor; any cloud host would be too. Cloudflare was only a demo and is being removed | Check Microsoft data-processing terms and tenant data location (you) |
| Art 30 record of processing | Not produced | I can draft from the data model for your DPO to adopt |
| Art 32 security of processing | Partly: access control, TLS, audit, backups defined; encryption at rest, restore evidence, logging operations open (F-02, F-04, F-09) | See roadmap |
| Art 33-34 breach notification (ICO within 72 hours) | Not covered | Incident and breach procedure with named people (you) |
| Art 35 DPIA | Not done | Required in practice because it holds staff data at scale; I can supply the technical description (you, DPO) |
| Chapter V international transfers | Depends on Microsoft tenant region and any hosting choice | Confirm UK/EEA data location (you) |
| ICO registration and fee | Organisational | You |

### Cyber Essentials (five controls) for the on-premises host

| Control | Needed on the host |
|---|---|
| Firewalls | Only 443 (and admin access) open; database and object store not reachable from the network |
| Secure configuration | Hardened server image; no default accounts; production settings guard already in the app |
| User access control | Named administrators, MFA, least privilege, regular access review |
| Malware protection | Endpoint protection on the host; attachments stay off until a scanner is chosen |
| Security update management | Documented patching of the OS, Docker, container base images and dependencies within the required time |

Cyber Essentials Plus adds an external technical test of the live system.

### ISO/IEC 27001:2022 (organisation-level) and 27701 (privacy extension)

Software cannot "be ISO 27001". The organisation needs an ISMS: scope, risk assessment and treatment, a Statement of Applicability, policies, supplier management, training, internal audit and management review. Helpdesk contributes evidence for Annex A technological controls (access control, cryptography, backup, logging, vulnerability management, secure development, configuration management, data deletion). Gaps above map to A.8.13 (backup), A.8.15-16 (logging, monitoring), A.8.24 (cryptography), A.8.8 (vulnerabilities) and A.8.10 (deletion).

### OWASP ASVS Level 2 (the stated verification target)

No formal ASVS run has been done. The controls above cover much of V2 (authentication via Entra), V3 (session), V4 (access control), V5 (input handling), V7 (logging, partial), V9-10 (communication, partial), V14 (configuration). A line-by-line self-assessment and an independent test are the way to claim it.

## 4. The path, in order

**Stage A: decisions only your organisation can make (this fortnight).**
1. Which framework, and certify or align: Cyber Essentials (+), ISO 27001 certification, or both. Read the audit finding in full and share the wording.
2. Hosting: dedicated on-premises server or VM (hardened, encrypted disk, restricted physical and admin access) versus a managed UK cloud. On-premises is acceptable if the controls below are evidenced.
3. Name the system owner, a security contact, and who carries out backups and patching.
4. Retention periods and lawful basis per data class (tickets, email bodies, audit, requester data); who the DPO is.
5. Malware scanner product, or accept "attachments stay off".
6. A Microsoft test tenant, a spare shared mailbox and a safe recipient.

**Stage B: build, with no new decisions needed (I can start these now, one at a time).**
1. Move the invitation token to the URL fragment and redact log paths (F-07).
2. Add rate limits for administration and webhook endpoints, and the missing response headers (F-06, F-10).
3. CI: dependency audit, secret scan, SBOM; digest-pin base images (F-05; any new tool needs your approval).
4. Draft the data inventory and record of processing, and the technical part of the DPIA.
5. Self-assess against ASVS Level 2 and record the result.

**Stage C: build after Stage A decisions.**
1. Retention jobs, subject-access export and erasure/anonymisation path (F-01).
2. Key protection outside the database for the Data Protection keys and the Entra secret (F-04).
3. Session lifetime and step-up rules (F-08).
4. Log retention and alert routing (F-09).

**Stage D: evidence on the real host before go-live.**
1. Deploy behind the reverse proxy with TLS; verify headers and WebSockets.
2. Run one backup and one full restore drill; record it (F-02).
3. Patch baseline, disk encryption and firewall evidence.
4. Independent penetration test and a container scan; fix and retest.
5. Live Graph and Entra validation in the test tenant (F-13).
6. Sign-off: DPO for the DPIA and privacy notice; system owner for the risk register; a written go-live decision that states the accepted limits (single instance, attachments off).

## 5. Verdict

Not ready for production with staff data today. The application itself is in better shape than the process around it: dependencies are clean, the isolation and authentication design is sound, and the production guards are strict. What blocks use is mostly evidence and decisions (F-01, F-02, F-03), plus three build items (retention and subject rights, key protection, supply-chain checks). On-premises hosting is a legitimate option provided Stage D evidence exists. The fastest way to a clear answer is Stage A items 1 and 4, because they decide how much of Stages B to D is needed.
