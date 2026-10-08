# Post-Refinement-10 remediation status

Short tracker. Findings and decisions: `POST-REFINEMENT-10-MASTER-REPORT.md`, `../plans/POST-REFINEMENT-10-DECISIONS.md`. Test runs are targeted only; no full suites. SDK note: .NET 10 runs in the `hd-sdk` container.

| WS | Status | Findings closed | Commits | Externally blocked / open | Tests run |
|----|--------|-----------------|---------|---------------------------|-----------|
| 1 Administrative isolation | Complete | SEC-001, SEC-005, SEC-012, ARC-002, QA-007 | c5b9d36 | - | (earlier) |
| 2 Ticket parties | Complete | MSG-002, SEC-006, MSG-003, BE-001, SEC-004, BE-002, ARC-003 | 368c683 | MSG-012 (token wording) open | (earlier) |
| 3 Mail delivery state | Complete (MSG-007 deferred) | MSG-001, BE-009, ARC-001, MSG-006, MSG-008, MSG-010 | a42d010 | MSG-007 forged bounce needs real headers (E-38, D-16); MSG-006 header shape needs E-37/E-38 | GraphDeliveryConfidenceTests, MailInboundTests (duplicate), EmailIdentityTests, QueuePreferencesTests, mail.test.tsx; migration DeliveryStatesUncertainBounced up/down/up |
| 4 Mailbox registration | Complete (tenant evidence open) | MSG-004 (code) | see git log | D-16 E-2/E-3 scoping proof | MailAdminApiTests, GraphProviderTests, mail.test.tsx |
| 5 Hosting / security hardening | Complete (deployment check open) | SRE-001, SRE-006 (confirmed + fixed), SRE-010, SRE-014, SEC-009 (confirmed + fixed), SEC-010, SEC-011 | see git log | Running the reference compose behind a real proxy/TLS and a browser CSP check need a deployment (not run); image build + smoke of the SPA from the image run locally | ProductionHostingTests, AuthConfigurationTests, MailRulesTests (data urls); host image build + smoke |
| 6 Recovery | Complete in code/docs; evidence open | SRE-002, SRE-003 (tooling + procedure), SRE-007, SRE-008, ARC-007 | see git log | No restore drill run (owner not named, D3); object-store backup is a provider procedure; encrypted-at-rest keys not implemented (DB is the boundary) | RecoveryTests; migration DataProtectionKeys up/down/up; backup/restore pipeline smoke (not a drill) |
| 7 Observability / operations | Complete except pruning | SRE-004, SRE-005 (signals; poison-row parking belongs to WS-9/BE-005), SRE-012, SRE-013 | see git log | SRE-011: pruning blocked on D-09 (not implemented), sizing estimates not written; SRE-009 reviewed, locks unchanged (stalls are now visible as job-lag metrics); real OTLP collector not exercised | OperationsTests, HealthEndpointTests |
